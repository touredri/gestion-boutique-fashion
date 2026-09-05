using System.Text.Json;
using BoutiqueFashion.Application;
using BoutiqueFashion.Contracts;
using BoutiqueFashion.Domain;
using BoutiqueFashion.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BoutiqueFashion.Tests;

/// <summary>
/// File de synchronisation. La propriété qui compte n'est pas qu'elle se remplisse, mais qu'elle
/// se remplisse <b>dans la même transaction</b> que la donnée décrite : une vente encaissée dont
/// l'événement manquerait ne remonterait jamais, et l'inverse ferait apparaître à distance une
/// vente qui n'a pas eu lieu.
/// </summary>
public sealed class SyncOutboxTests : IAsyncLifetime
{
    private const string ManagerPin = "123456";
    private readonly string root = Path.Combine(Path.GetTempPath(), $"boutique-sync-{Guid.NewGuid():N}");
    private ServiceProvider provider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddBoutiqueInfrastructure(root);
        provider = services.BuildServiceProvider();
        await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        await provider.GetRequiredService<IAuthorizationService>().ConfigurePinAsync(ManagerPin);
        await provider.GetRequiredService<ICashSessionService>().OpenAsync(10_000, "Awa", "4321");
    }

    public Task DisposeAsync()
    {
        provider.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private async Task<List<SyncOutboxEntry>> OutboxAsync()
    {
        await using var db = await provider.GetRequiredService<IDbContextFactory<BoutiqueDbContext>>().CreateDbContextAsync();
        return await db.SyncOutbox.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync();
    }

    private static T Payload<T>(SyncOutboxEntry entry) =>
        JsonSerializer.Deserialize<T>(entry.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public async Task Opening_the_till_queues_the_event()
    {
        var entries = await OutboxAsync();
        var opened = Assert.Single(entries, x => x.EntityType == SyncEntityTypes.CashSessionOpened);

        var payload = Payload<CashSessionOpenedPayload>(opened);
        Assert.Equal("Awa", payload.OperatorName);
        Assert.Equal(10_000, payload.OpeningFloatXof);
    }

    [Fact]
    public async Task The_shift_pin_hash_never_leaves_the_terminal()
    {
        // Le condensé du code n'a aucun usage à distance ; l'expédier n'apporterait que du risque.
        var entries = await OutboxAsync();
        Assert.All(entries, x => Assert.DoesNotContain("PinHash", x.PayloadJson, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_sale_travels_as_one_event_with_its_lines_and_payments()
    {
        var article = await provider.GetRequiredService<ICatalogService>()
            .CreateVariantAsync("Robe", "Vêtements", "SYNC-01", null, "M", "Rouge", 10_000, 20_000, 5, 0);
        var result = await provider.GetRequiredService<ISaleService>().CreateAsync(new SaleDraft(
            "sync-vente", [new SaleLineDraft(article.Id, 2)], [new PaymentDraft(PaymentMode.Cash, 40_000)]));

        var entry = Assert.Single(await OutboxAsync(), x => x.EntityType == SyncEntityTypes.Sale);
        Assert.Equal(result.SaleId, entry.EntityId);

        var payload = Payload<SalePayload>(entry);
        Assert.Equal(result.Number, payload.Number);
        Assert.Equal("Awa", payload.SellerName);
        Assert.Equal(40_000, payload.TotalXof);
        Assert.Single(payload.Lines);
        Assert.Equal(2, payload.Lines[0].Quantity);
        Assert.Single(payload.Payments);
        Assert.Null(payload.Credit);
    }

    [Fact]
    public async Task A_reserved_advance_carries_its_credit()
    {
        var article = await provider.GetRequiredService<ICatalogService>()
            .CreateVariantAsync("Ensemble", "Vêtements", "SYNC-02", null, "L", "Bleu", 10_000, 30_000, 3, 0);
        var customer = await provider.GetRequiredService<ICustomerService>().CreateAsync("Fanta", "0700000001", 0);

        await provider.GetRequiredService<ISaleService>().CreateAsync(new SaleDraft(
            "sync-avance", [new SaleLineDraft(article.Id, 1)],
            [new PaymentDraft(PaymentMode.Cash, 10_000), new PaymentDraft(PaymentMode.Credit, 20_000)],
            customer.Id, CreditDueAt: DateTimeOffset.Now.AddDays(30), ReserveStock: true));

        var payload = Payload<SalePayload>(Assert.Single(await OutboxAsync(), x => x.EntityType == SyncEntityTypes.Sale));
        Assert.Equal(SaleStatus.Reserved, payload.Status);
        Assert.NotNull(payload.Credit);
        Assert.Equal(20_000, payload.Credit.OriginalAmountXof);
    }

    [Fact]
    public async Task A_refused_sale_leaves_nothing_behind()
    {
        var article = await provider.GetRequiredService<ICatalogService>()
            .CreateVariantAsync("Sac", "Accessoires", "SYNC-03", null, null, "Noir", 5_000, 10_000, 2, 0);
        var before = (await OutboxAsync()).Count;

        // Paiement insuffisant : la vente est rejetée, donc rien ne doit être annoncé au serveur.
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRequiredService<ISaleService>().CreateAsync(
            new SaleDraft("sync-refus", [new SaleLineDraft(article.Id, 1)], [new PaymentDraft(PaymentMode.Cash, 4_000)])));

        Assert.Equal(before, (await OutboxAsync()).Count);
        Assert.DoesNotContain(await OutboxAsync(), x => x.EntityType == SyncEntityTypes.Sale);
    }

    [Fact]
    public async Task An_idempotent_replay_does_not_queue_the_sale_twice()
    {
        var article = await provider.GetRequiredService<ICatalogService>()
            .CreateVariantAsync("Chemise", "Vêtements", "SYNC-04", null, "M", "Blanc", 5_000, 10_000, 5, 0);
        var draft = new SaleDraft("sync-idem", [new SaleLineDraft(article.Id, 1)], [new PaymentDraft(PaymentMode.Cash, 10_000)]);

        await provider.GetRequiredService<ISaleService>().CreateAsync(draft);
        await provider.GetRequiredService<ISaleService>().CreateAsync(draft);

        Assert.Single(await OutboxAsync(), x => x.EntityType == SyncEntityTypes.Sale);
    }

    [Fact]
    public async Task Cash_movements_expenses_and_instalments_are_queued()
    {
        var cash = provider.GetRequiredService<ICashSessionService>();
        await cash.RecordMovementAsync(CashMovementDirection.Out, 3_000, "Achat de monnaie");
        await provider.GetRequiredService<IExpenseService>().CreateAsync("Transport", "Taxi", 2_000, PaymentMode.Cash);

        var article = await provider.GetRequiredService<ICatalogService>()
            .CreateVariantAsync("Montre", "Accessoires", "SYNC-05", null, null, "Or", 10_000, 30_000, 2, 0);
        var customer = await provider.GetRequiredService<ICustomerService>().CreateAsync("Salif", "0700000002", 100_000);
        await provider.GetRequiredService<ISaleService>().CreateAsync(new SaleDraft(
            "sync-credit", [new SaleLineDraft(article.Id, 1)], [new PaymentDraft(PaymentMode.Credit, 30_000)],
            customer.Id, ManagerPin: ManagerPin, CreditDueAt: DateTimeOffset.Now.AddDays(30)));
        var credit = (await provider.GetRequiredService<ICreditService>().ListAsync()).Single();
        await provider.GetRequiredService<ICreditService>().PayAsync(credit.Id, 10_000, PaymentMode.Cash, null);

        var types = (await OutboxAsync()).Select(x => x.EntityType).ToList();
        Assert.Contains(SyncEntityTypes.CashMovement, types);
        Assert.Contains(SyncEntityTypes.Expense, types);
        Assert.Contains(SyncEntityTypes.CreditPayment, types);
        Assert.Contains(SyncEntityTypes.Customer, types);
    }

    [Fact]
    public async Task Every_stock_movement_is_queued_whatever_its_origin()
    {
        // Interception au SaveChanges plutôt qu'appel explicite : les mouvements naissent en six
        // endroits, et il suffirait d'en oublier un pour que le stock affiché à distance dérive.
        var catalog = provider.GetRequiredService<ICatalogService>();
        var article = await catalog.CreateVariantAsync("Botte", "Chaussures", "SYNC-06", null, "40", "Noir", 10_000, 25_000, 5, 0);
        await provider.GetRequiredService<IStockService>().AdjustAsync(
            new StockAdjustment(article.Id, -1, StockMovementType.Damaged, 10_000, "Abîmée", "Awa"), ManagerPin);
        await provider.GetRequiredService<IInventoryService>().ApplyCountAsync(
            [new InventoryCount(article.Id, 3)], "Comptage", ManagerPin);

        var types = (await OutboxAsync())
            .Where(x => x.EntityType == SyncEntityTypes.StockMovement)
            .Select(x => JsonSerializer.Deserialize<StockMovementPayload>(x.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Type)
            .ToList();
        Assert.Contains(StockMovementType.Damaged, types);
        Assert.Contains(StockMovementType.Inventory, types);
    }

    [Fact]
    public async Task Handing_over_a_reserved_advance_reannounces_the_sale()
    {
        var article = await provider.GetRequiredService<ICatalogService>()
            .CreateVariantAsync("Boubou", "Vêtements", "SYNC-07", null, "L", "Or", 10_000, 30_000, 3, 0);
        var customer = await provider.GetRequiredService<ICustomerService>().CreateAsync("Aïcha", "0700000003", 0);
        var sale = await Sales.CreateAsync(new SaleDraft(
            "sync-remise", [new SaleLineDraft(article.Id, 1)],
            [new PaymentDraft(PaymentMode.Cash, 10_000), new PaymentDraft(PaymentMode.Credit, 20_000)],
            customer.Id, CreditDueAt: DateTimeOffset.Now.AddDays(30), ReserveStock: true));

        var credit = (await provider.GetRequiredService<ICreditService>().ListAsync()).Single(x => x.SaleNumber == sale.Number);
        await provider.GetRequiredService<ICreditService>().PayAsync(credit.Id, 20_000, PaymentMode.Cash, null);

        // Sans ce second événement, l'avance resterait éternellement « réservée » côté serveur
        // et son chiffre d'affaires manquerait sur le téléphone.
        var announcements = (await OutboxAsync()).Where(x => x.EntityType == SyncEntityTypes.Sale && x.EntityId == sale.SaleId).ToList();
        Assert.Equal(2, announcements.Count);
        Assert.Equal(SaleStatus.Completed, Payload<SalePayload>(announcements[^1]).Status);
    }

    [Fact]
    public async Task Cancelling_a_sale_reannounces_it_as_cancelled()
    {
        var article = await provider.GetRequiredService<ICatalogService>()
            .CreateVariantAsync("Écharpe", "Accessoires", "SYNC-08", null, null, "Rouge", 3_000, 8_000, 4, 0);
        var sale = await Sales.CreateAsync(new SaleDraft(
            "sync-annulation", [new SaleLineDraft(article.Id, 1)], [new PaymentDraft(PaymentMode.Cash, 8_000)]));

        await provider.GetRequiredService<IReturnService>().CancelSaleAsync(sale.Number, "Erreur de saisie", ManagerPin);

        // Une vente annulée restée « validée » à distance gonflerait le chiffre d'affaires.
        var announcements = (await OutboxAsync()).Where(x => x.EntityType == SyncEntityTypes.Sale && x.EntityId == sale.SaleId).ToList();
        Assert.Equal(SaleStatus.Cancelled, Payload<SalePayload>(announcements[^1]).Status);
    }

    [Fact]
    public async Task Closing_the_till_queues_the_counted_figures()
    {
        await provider.GetRequiredService<ICashSessionService>().CloseAsync(10_000, null, "4321");

        var payload = Payload<CashSessionClosedPayload>(
            Assert.Single(await OutboxAsync(), x => x.EntityType == SyncEntityTypes.CashSessionClosed));
        Assert.Equal(10_000, payload.CountedCashXof);
        Assert.Equal(0, payload.DifferenceXof);
        Assert.Equal("Awa", payload.ClosedBy);
    }

    [Fact]
    public async Task Every_queued_event_is_unsent_and_carries_readable_json()
    {
        var entries = await OutboxAsync();
        Assert.NotEmpty(entries);
        Assert.All(entries, x =>
        {
            Assert.Null(x.SentAt);
            Assert.Equal(0, x.AttemptCount);
            Assert.NotEqual(Guid.Empty, x.EntityId);
            using var parsed = JsonDocument.Parse(x.PayloadJson);
            Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
        });
    }
}
