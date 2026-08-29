using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using BoutiqueFashion.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BoutiqueFashion.Tests;

public sealed class SaleIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"boutique-tests-{Guid.NewGuid():N}");
    private ServiceProvider provider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection(); services.AddBoutiqueInfrastructure(root); provider = services.BuildServiceProvider();
        await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        await provider.GetRequiredService<IAuthorizationService>().ConfigurePinAsync("123456");
        await provider.GetRequiredService<ICashSessionService>().OpenAsync(10_000);
    }

    public Task DisposeAsync() { provider.Dispose(); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); return Task.CompletedTask; }

    [Fact]
    public async Task Sale_is_atomic_decrements_stock_and_is_idempotent()
    {
        var variant = await provider.GetRequiredService<ICatalogService>().CreateVariantAsync("Chemise", "Vêtements", "CHE-M-BLA", "1001", "M", "Blanc", 8_000, 15_000, 2, 1);
        var service = provider.GetRequiredService<ISaleService>();
        var draft = new SaleDraft("same-key", [new SaleLineDraft(variant.Id, 1)], [new PaymentDraft(PaymentMode.Cash, 15_000)]);
        var first = await service.CreateAsync(draft); var second = await service.CreateAsync(draft);
        Assert.False(first.AlreadyExisted); Assert.True(second.AlreadyExisted); Assert.Equal(first.SaleId, second.SaleId);
        var refreshed = (await provider.GetRequiredService<ICatalogService>().SearchAsync("CHE-M-BLA")).Single(); Assert.Equal(1, refreshed.QuantityOnHand);
    }

    [Fact]
    public async Task Payment_sum_must_match_sale_total()
    {
        var variant = await provider.GetRequiredService<ICatalogService>().CreateVariantAsync("Sac", "Accessoires", "SAC-01", null, null, "Noir", 5_000, 10_000, 1, 0);
        var draft = new SaleDraft("bad-payment", [new SaleLineDraft(variant.Id, 1)], [new PaymentDraft(PaymentMode.Cash, 9_000)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<ISaleService>().CreateAsync(draft));
        var refreshed = (await provider.GetRequiredService<ICatalogService>().SearchAsync("SAC-01")).Single(); Assert.Equal(1, refreshed.QuantityOnHand);
    }

    [Fact]
    public async Task Backup_is_created_and_valid()
    {
        var backup = provider.GetRequiredService<IBackupService>(); var path = await backup.CreateAsync(); Assert.True(File.Exists(path)); Assert.True(await backup.VerifyAsync(path));
    }

    [Fact]
    public async Task Mixed_payment_and_inventory_count_are_persisted()
    {
        var variant = await provider.GetRequiredService<ICatalogService>().CreateVariantAsync("Robe", "Vêtements", "ROB-01", null, "M", "Rouge", 10_000, 20_000, 4, 1);
        var result = await provider.GetRequiredService<ISaleService>().CreateAsync(new SaleDraft("mixed", [new SaleLineDraft(variant.Id, 1)], [new PaymentDraft(PaymentMode.Cash, 5_000), new PaymentDraft(PaymentMode.Wave, 15_000)]));
        Assert.Equal(20_000, result.TotalXof);
        await provider.GetRequiredService<IInventoryService>().ApplyCountAsync([new InventoryCount(variant.Id, 7)], "Comptage test", "123456");
        Assert.Equal(7, (await provider.GetRequiredService<ICatalogService>().SearchAsync("ROB-01")).Single().QuantityOnHand);
    }

    [Fact]
    public async Task Credit_payment_updates_balance()
    {
        var customer = await provider.GetRequiredService<ICustomerService>().CreateAsync("Awa", "70000000", 100_000);
        var variant = await provider.GetRequiredService<ICatalogService>().CreateVariantAsync("Sac crédit", "Accessoires", "SAC-CR", null, null, "Noir", 10_000, 30_000, 2, 0);
        await provider.GetRequiredService<ISaleService>().CreateAsync(new SaleDraft("credit", [new SaleLineDraft(variant.Id, 1)], [new PaymentDraft(PaymentMode.Credit, 30_000)], customer.Id, ManagerPin: "123456", CreditDueAt: DateTimeOffset.Now.AddDays(30)));
        var credit = (await provider.GetRequiredService<ICreditService>().ListAsync()).Single();
        var payment = await provider.GetRequiredService<ICreditService>().PayAsync(credit.Id, 10_000, PaymentMode.Wave, "WAVE-1");
        Assert.Equal(20_000, payment.NewBalanceXof);
    }

    [Fact]
    public async Task Matrix_creates_unique_variants_per_color_and_size()
    {
        var created = await provider.GetRequiredService<ICatalogService>().CreateMatrixAsync(new MatrixDraft("Chemise Classic", "Vêtements", "CHE-CLA", ["Blanc", "Bleu"], ["M", "L", "X"], 8_000, 15_000, 2, 1));
        Assert.Equal(6, created.Count);
        Assert.Equal(6, created.Select(x => x.Sku).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(created, x => Assert.Equal(2, x.QuantityOnHand));
    }

    [Fact]
    public async Task Change_is_computed_and_cash_payment_recorded_net()
    {
        var variant = await provider.GetRequiredService<ICatalogService>().CreateVariantAsync("Pantalon", "Vêtements", "PAN-01", null, "M", "Noir", 10_000, 20_000, 4, 0);
        var result = await provider.GetRequiredService<ISaleService>().CreateAsync(new SaleDraft("change", [new SaleLineDraft(variant.Id, 1)], [new PaymentDraft(PaymentMode.Cash, 25_000)]));
        Assert.Equal(5_000, result.ChangeXof);
        await using var db = await provider.GetRequiredService<IDbContextFactory<BoutiqueDbContext>>().CreateDbContextAsync();
        var sale = await db.Sales.Include(x => x.Payments).SingleAsync(x => x.Id == result.SaleId);
        Assert.Equal(5_000, sale.ChangeXof);
        Assert.Single(sale.Payments);
        Assert.Equal(20_000, sale.Payments.Single().AmountXof);
    }

    [Fact]
    public async Task Auto_customer_is_created_and_walk_in_sale_stays_unlinked()
    {
        var variant = await provider.GetRequiredService<ICatalogService>().CreateVariantAsync("Foulard", "Accessoires", "FOU-01", null, null, "Orange", 2_000, 5_000, 5, 0);
        await provider.GetRequiredService<ISaleService>().CreateAsync(new SaleDraft("auto-client", [new SaleLineDraft(variant.Id, 1)], [new PaymentDraft(PaymentMode.Cash, 5_000)], NewCustomerName: "Aminata", NewCustomerPhone: "77000011"));
        var rows = await provider.GetRequiredService<ICustomerService>().SearchAsync("Aminata");
        Assert.Single(rows);
        var walkIn = await provider.GetRequiredService<ISaleService>().CreateAsync(new SaleDraft("walk-in", [new SaleLineDraft(variant.Id, 1)], [new PaymentDraft(PaymentMode.Cash, 5_000)]));
        await using var db = await provider.GetRequiredService<IDbContextFactory<BoutiqueDbContext>>().CreateDbContextAsync();
        Assert.Null((await db.Sales.SingleAsync(x => x.Id == walkIn.SaleId)).CustomerId);
    }

    [Fact]
    public async Task Close_cash_beyond_tolerance_requires_manager_pin()
    {
        var cash = provider.GetRequiredService<ICashSessionService>();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => cash.CloseAsync(9_000, "écart de caisse", null));
        var closed = await cash.CloseAsync(9_000, "écart de caisse", "123456");
        Assert.Equal(-1_000, closed.DifferenceXof);
    }

    [Fact]
    public async Task Credit_sale_with_deposit_issues_receipt_invoice_and_deposit_documents()
    {
        var customer = await provider.GetRequiredService<ICustomerService>().CreateAsync("Kofi", "70000022", 100_000);
        var variant = await provider.GetRequiredService<ICatalogService>().CreateVariantAsync("Veste", "Vêtements", "VES-01", null, "L", "Vert", 15_000, 30_000, 3, 0);
        var result = await provider.GetRequiredService<ISaleService>().CreateAsync(new SaleDraft("acompte", [new SaleLineDraft(variant.Id, 1)], [new PaymentDraft(PaymentMode.Cash, 10_000), new PaymentDraft(PaymentMode.Credit, 20_000)], customer.Id, ManagerPin: "123456", CreditDueAt: DateTimeOffset.Now.AddDays(30)));
        await using var db = await provider.GetRequiredService<IDbContextFactory<BoutiqueDbContext>>().CreateDbContextAsync();
        var types = await db.DocumentSnapshots.Where(x => x.SaleId == result.SaleId).Select(x => x.Type).ToListAsync();
        Assert.Contains(DocumentType.Receipt, types);
        Assert.Contains(DocumentType.Invoice, types);
        Assert.Contains(DocumentType.DepositReceipt, types);
    }

    [Fact]
    public async Task Full_credit_payment_issues_balance_receipt_and_return_issues_return_note()
    {
        var customer = await provider.GetRequiredService<ICustomerService>().CreateAsync("Moussa", "70000033", 100_000);
        var variant = await provider.GetRequiredService<ICatalogService>().CreateVariantAsync("Ceinture", "Accessoires", "CEI-01", null, null, "Marron", 3_000, 10_000, 4, 0);
        var creditSale = await provider.GetRequiredService<ISaleService>().CreateAsync(new SaleDraft("credit-solde", [new SaleLineDraft(variant.Id, 1)], [new PaymentDraft(PaymentMode.Credit, 10_000)], customer.Id, ManagerPin: "123456", CreditDueAt: DateTimeOffset.Now.AddDays(30)));
        var credit = (await provider.GetRequiredService<ICreditService>().ListAsync()).Single(x => x.SaleNumber == creditSale.Number);
        await provider.GetRequiredService<ICreditService>().PayAsync(credit.Id, 10_000, PaymentMode.Cash, null);
        var cashSale = await provider.GetRequiredService<ISaleService>().CreateAsync(new SaleDraft("retour-note", [new SaleLineDraft(variant.Id, 1)], [new PaymentDraft(PaymentMode.Cash, 10_000)]));
        await provider.GetRequiredService<IReturnService>().ReturnOrExchangeAsync(new ReturnRequest(cashSale.Number, "CEI-01", 1, null, 0, [], "Taille inadaptée", "123456"));
        await using var db = await provider.GetRequiredService<IDbContextFactory<BoutiqueDbContext>>().CreateDbContextAsync();
        var allTypes = await db.DocumentSnapshots.Select(x => x.Type).ToListAsync();
        Assert.Contains(DocumentType.BalanceReceipt, allTypes);
        Assert.Contains(DocumentType.ReturnNote, allTypes);
    }

    [Fact]
    public async Task Full_inventory_applies_multiple_counts_at_once()
    {
        var a = await provider.GetRequiredService<ICatalogService>().CreateVariantAsync("Tee A", "Vêtements", "TEE-A", null, "M", "Blanc", 3_000, 6_000, 5, 0);
        var b = await provider.GetRequiredService<ICatalogService>().CreateVariantAsync("Tee B", "Vêtements", "TEE-B", null, "M", "Noir", 3_000, 6_000, 5, 0);
        await provider.GetRequiredService<IInventoryService>().ApplyCountAsync([new InventoryCount(a.Id, 4), new InventoryCount(b.Id, 7)], "Inventaire complet", "123456");
        var catalog = provider.GetRequiredService<ICatalogService>();
        Assert.Equal(4, (await catalog.SearchAsync("TEE-A")).Single().QuantityOnHand);
        Assert.Equal(7, (await catalog.SearchAsync("TEE-B")).Single().QuantityOnHand);
    }

    [Fact]
    public async Task Stock_alerts_list_low_and_negative_with_related_sale()
    {
        var catalog = provider.GetRequiredService<ICatalogService>();
        var low = await catalog.CreateVariantAsync("Alerte basse", "Vêtements", "ALR-LOW", null, "M", "Blanc", 3_000, 6_000, 2, 3);
        var negative = await catalog.CreateVariantAsync("Alerte négative", "Vêtements", "ALR-NEG", null, "M", "Noir", 3_000, 6_000, 1, 0);
        await provider.GetRequiredService<ISaleService>().CreateAsync(new SaleDraft("vente-negative", [new SaleLineDraft(negative.Id, 2)], [new PaymentDraft(PaymentMode.Cash, 12_000)]));
        var alerts = await provider.GetRequiredService<IReportService>().StockAlertsAsync();
        Assert.Contains(alerts, x => x.Sku == "ALR-LOW" && x.Kind == "Stock faible");
        var negativeAlert = alerts.Single(x => x.Sku == "ALR-NEG");
        Assert.Equal("Négatif (à fournir)", negativeAlert.Kind);
        Assert.NotNull(negativeAlert.RelatedSale);
    }

    [Fact]
    public async Task Reports_by_day_and_cash_closings_reflect_activity()
    {
        var variant = await provider.GetRequiredService<ICatalogService>().CreateVariantAsync("Bob", "Accessoires", "BOB-01", null, null, "Beige", 2_000, 8_000, 3, 0);
        await provider.GetRequiredService<ISaleService>().CreateAsync(new SaleDraft("rapport-jour", [new SaleLineDraft(variant.Id, 2)], [new PaymentDraft(PaymentMode.Cash, 16_000)]));
        var reports = provider.GetRequiredService<IReportService>();
        var from = DateTimeOffset.Now.AddDays(-1); var to = DateTimeOffset.Now.AddDays(1);
        var byDay = await reports.SalesByDayAsync(from, to);
        Assert.Single(byDay);
        Assert.Equal(16_000, byDay[0].ValueXof);
        var closed = await provider.GetRequiredService<ICashSessionService>().CloseAsync(26_000, null, "123456");
        var closings = await reports.CashClosingsAsync(from, to);
        Assert.Contains(closings, x => x.Number == closed.Number);
    }

    [Fact]
    public async Task Change_pin_rotates_credentials()
    {
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        await authorization.ChangePinAsync("123456", "654321");
        Assert.True(await authorization.AuthorizeSensitiveActionAsync("654321", "Test rotation"));
        Assert.False(await authorization.AuthorizeSensitiveActionAsync("123456", "Test rotation ancien"));
    }

    [Fact]
    public async Task Customer_segments_reflect_debtor_and_new_states()
    {
        var customers = provider.GetRequiredService<ICustomerService>();
        var newCustomer = await customers.CreateAsync("Fatou", "77000044", 0);
        var debtor = await customers.CreateAsync("Idriss", "77000055", 50_000);
        var variant = await provider.GetRequiredService<ICatalogService>().CreateVariantAsync("Montre", "Accessoires", "MON-01", null, null, "Doré", 10_000, 25_000, 2, 0);
        await provider.GetRequiredService<ISaleService>().CreateAsync(new SaleDraft("client-debiteur", [new SaleLineDraft(variant.Id, 1)], [new PaymentDraft(PaymentMode.Credit, 25_000)], debtor.Id, ManagerPin: "123456", CreditDueAt: DateTimeOffset.Now.AddDays(30)));
        var rows = await customers.SearchAsync(null);
        Assert.Equal(CustomerSegment.New, rows.Single(x => x.Name == "Fatou").Segment);
        Assert.Equal(CustomerSegment.Debtor, rows.Single(x => x.Name == "Idriss").Segment);
    }
}
