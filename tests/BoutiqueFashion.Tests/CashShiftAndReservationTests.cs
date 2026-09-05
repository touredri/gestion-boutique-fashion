using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using BoutiqueFashion.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BoutiqueFashion.Tests;

/// <summary>
/// Vacation de caisse et avances réservées. Le PIN gérant vaut « 123456 » et une caisse est
/// ouverte au nom d'« Awa » avec le code de vacation « 4321 » avant chaque test.
/// </summary>
public sealed class CashShiftAndReservationTests : IAsyncLifetime
{
    private const string ManagerPin = "123456";
    private const string ShiftPin = "4321";
    private readonly string root = Path.Combine(Path.GetTempPath(), $"boutique-shift-{Guid.NewGuid():N}");
    private ServiceProvider provider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddBoutiqueInfrastructure(root);
        provider = services.BuildServiceProvider();
        await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        await provider.GetRequiredService<IAuthorizationService>().ConfigurePinAsync(ManagerPin);
        await provider.GetRequiredService<ICashSessionService>().OpenAsync(10_000, "Awa", ShiftPin);
    }

    public Task DisposeAsync()
    {
        provider.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    private ICatalogService Catalog => provider.GetRequiredService<ICatalogService>();
    private ISaleService Sales => provider.GetRequiredService<ISaleService>();
    private ICashSessionService Cash => provider.GetRequiredService<ICashSessionService>();
    private ICreditService Credits => provider.GetRequiredService<ICreditService>();

    private Task<ProductVariant> AddArticleAsync(string name, string sku, decimal quantity) =>
        Catalog.CreateVariantAsync(name, "Vêtements", sku, null, "M", "Noir", 10_000, 20_000, quantity, 0);

    private async Task<ProductVariant> ReloadAsync(string sku) => (await Catalog.SearchAsync(sku)).Single();

    // --- Vacation de caisse ------------------------------------------------

    [Fact]
    public async Task Sale_is_attributed_to_the_shift_operator()
    {
        var article = await AddArticleAsync("Chemise", "SHIFT-01", 5);
        var sale = await Sales.CreateAsync(new SaleDraft("vente-vacation", [new SaleLineDraft(article.Id, 1)], [new PaymentDraft(PaymentMode.Cash, 20_000)]));

        await using var db = await provider.GetRequiredService<IDbContextFactory<BoutiqueDbContext>>().CreateDbContextAsync();
        Assert.Equal("Awa", (await db.Sales.SingleAsync(x => x.Id == sale.SaleId)).SellerName);
    }

    [Fact]
    public async Task Shift_operator_defaults_to_the_shop_name()
    {
        await Cash.CloseAsync(10_000, null, ShiftPin);
        await provider.GetRequiredService<IAppSettingsService>().SetAsync("Shop.Name", "Boutique Marcory");
        var session = await Cash.OpenAsync(0);

        Assert.Equal("Boutique Marcory", session.OperatorName);
    }

    [Fact]
    public async Task Shift_pin_closes_the_till_without_the_manager_pin()
    {
        var closed = await Cash.CloseAsync(10_000, null, ShiftPin);

        Assert.Equal(0, closed.DifferenceXof);
        Assert.Equal("Awa", closed.ClosedBy);
        Assert.Equal(CashSessionStatus.Closed, closed.Status);
    }

    [Fact]
    public async Task Wrong_pin_cannot_close_the_till()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Cash.CloseAsync(10_000, null, "0000"));
        Assert.NotNull(await Cash.GetOpenAsync());
    }

    [Fact]
    public async Task Shift_pin_is_not_enough_when_the_variance_exceeds_tolerance()
    {
        // L'écart reste une décision du gérant : le code de vacation ne suffit plus.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Cash.CloseAsync(8_000, "billet manquant", ShiftPin));
        var closed = await Cash.CloseAsync(8_000, "billet manquant", ManagerPin);

        Assert.Equal(-2_000, closed.DifferenceXof);
        Assert.Equal("Responsable", closed.ClosedBy);
    }

    [Fact]
    public async Task Shift_pin_verification_only_matches_the_open_session()
    {
        Assert.True(await Cash.VerifyShiftPinAsync(ShiftPin));
        Assert.False(await Cash.VerifyShiftPinAsync("9999"));
    }

    [Fact]
    public async Task Live_state_matches_what_the_closing_expects()
    {
        var article = await AddArticleAsync("Jupe", "SHIFT-02", 5);
        await Sales.CreateAsync(new SaleDraft("etat-1", [new SaleLineDraft(article.Id, 1)], [new PaymentDraft(PaymentMode.Cash, 20_000)]));
        await Sales.CreateAsync(new SaleDraft("etat-2", [new SaleLineDraft(article.Id, 1)], [new PaymentDraft(PaymentMode.Wave, 20_000)]));
        await provider.GetRequiredService<IExpenseService>().CreateAsync("Transport", "Taxi", 5_000, PaymentMode.Cash);

        var state = await Cash.GetStateAsync();
        Assert.NotNull(state);
        Assert.Equal("Awa", state.OperatorName);
        Assert.True(state.HasShiftPin);
        Assert.Equal(20_000, state.CashSalesXof);
        Assert.Equal(5_000, state.CashExpensesXof);
        Assert.Equal(25_000, state.ExpectedCashXof); // 10 000 fond + 20 000 espèces - 5 000 dépense
        Assert.Equal(2, state.SalesCount);

        // L'affichage temps réel et la clôture doivent partager la même formule : sans quoi le
        // vendeur compte juste et se voit reprocher un écart.
        var closed = await Cash.CloseAsync(state.ExpectedCashXof, null, ShiftPin);
        Assert.Equal(0, closed.DifferenceXof);
    }

    // --- Avances réservées -------------------------------------------------

    private async Task<(ProductVariant Article, SaleResult Sale, Guid CreditId)> ReserveAsync(string sku, decimal quantity = 1)
    {
        var article = await AddArticleAsync("Robe de fête", sku, 3);
        // Le téléphone porte un index unique : on le dérive du SKU plutôt que d'un hachage,
        // dont la valeur varie d'une exécution à l'autre en .NET.
        var phone = "770000" + new string([.. sku.Where(char.IsDigit)]).PadLeft(4, '0');
        var customer = await provider.GetRequiredService<ICustomerService>().CreateAsync($"Cliente {sku}", phone, 0);
        var total = 20_000 * quantity;
        var sale = await Sales.CreateAsync(new SaleDraft(
            $"avance-{sku}",
            [new SaleLineDraft(article.Id, quantity)],
            [new PaymentDraft(PaymentMode.Cash, 5_000), new PaymentDraft(PaymentMode.Credit, (long)total - 5_000)],
            customer.Id,
            CreditDueAt: DateTimeOffset.Now.AddDays(30),
            ReserveStock: true));
        var credit = (await Credits.ListAsync()).Single(x => x.SaleNumber == sale.Number);
        return (article, sale, credit.Id);
    }

    [Fact]
    public async Task Reserved_advance_blocks_stock_without_removing_it()
    {
        var (_, _, _) = await ReserveAsync("RES-01");

        var article = await ReloadAsync("RES-01");
        Assert.Equal(3, article.QuantityOnHand);     // la marchandise est toujours en boutique
        Assert.Equal(1, article.QuantityReserved);
        Assert.Equal(2, article.QuantityAvailable);  // mais une pièce n'est plus vendable
    }

    [Fact]
    public async Task Reserved_advance_needs_neither_manager_pin_nor_credit_limit()
    {
        // Le client créé plus haut a un plafond de crédit nul et aucun PIN n'est fourni :
        // sans l'assouplissement, cette vente serait refusée deux fois.
        var (_, sale, _) = await ReserveAsync("RES-02");
        Assert.False(sale.AlreadyExisted);
    }

    [Fact]
    public async Task Reserving_more_than_available_is_refused()
    {
        var article = await AddArticleAsync("Écharpe", "RES-03", 1);
        var customer = await provider.GetRequiredService<ICustomerService>().CreateAsync("Trop", "7000000099", 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.CreateAsync(new SaleDraft(
            "avance-trop", [new SaleLineDraft(article.Id, 2)],
            [new PaymentDraft(PaymentMode.Cash, 5_000), new PaymentDraft(PaymentMode.Credit, 35_000)],
            customer.Id, CreditDueAt: DateTimeOffset.Now.AddDays(30), ReserveStock: true)));

        Assert.Equal(1, (await ReloadAsync("RES-03")).QuantityOnHand);
    }

    [Fact]
    public async Task Advance_without_remaining_balance_is_refused()
    {
        var article = await AddArticleAsync("Sac", "RES-04", 3);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sales.CreateAsync(new SaleDraft(
            "avance-sans-solde", [new SaleLineDraft(article.Id, 1)],
            [new PaymentDraft(PaymentMode.Cash, 20_000)], ReserveStock: true)));
    }

    [Fact]
    public async Task Final_instalment_hands_the_goods_over()
    {
        var (_, sale, creditId) = await ReserveAsync("RES-05");
        await Credits.PayAsync(creditId, 15_000, PaymentMode.Cash, null);

        var article = await ReloadAsync("RES-05");
        Assert.Equal(2, article.QuantityOnHand);     // le stock sort enfin
        Assert.Equal(0, article.QuantityReserved);   // la réservation est levée
        Assert.Equal(2, article.QuantityAvailable);

        await using var db = await provider.GetRequiredService<IDbContextFactory<BoutiqueDbContext>>().CreateDbContextAsync();
        Assert.Equal(SaleStatus.Completed, (await db.Sales.SingleAsync(x => x.Id == sale.SaleId)).Status);
    }

    [Fact]
    public async Task Partial_instalment_leaves_the_goods_reserved()
    {
        var (_, _, creditId) = await ReserveAsync("RES-06");
        await Credits.PayAsync(creditId, 5_000, PaymentMode.Cash, null);

        var article = await ReloadAsync("RES-06");
        Assert.Equal(3, article.QuantityOnHand);
        Assert.Equal(1, article.QuantityReserved);
    }

    [Fact]
    public async Task Stock_movements_balance_out_over_the_life_of_a_reservation()
    {
        var (article, _, creditId) = await ReserveAsync("RES-07");
        await Credits.PayAsync(creditId, 15_000, PaymentMode.Cash, null);

        await using var db = await provider.GetRequiredService<IDbContextFactory<BoutiqueDbContext>>().CreateDbContextAsync();
        var movements = await db.StockMovements.Where(x => x.VariantId == article.Id).ToListAsync();

        // Mise de côté (-1), levée (+1), vente (-1) : le cumul doit valoir la quantité réellement
        // vendue, sinon tout rapport qui somme l'historique compterait deux fois.
        Assert.Equal(-1, movements.Sum(x => x.QuantityDelta));
        Assert.Contains(movements, x => x.Type == StockMovementType.Reservation);
        Assert.Contains(movements, x => x.Type == StockMovementType.ReservationRelease);
        Assert.Contains(movements, x => x.Type == StockMovementType.Sale);
    }

    [Fact]
    public async Task Cancelling_a_reservation_releases_it_without_inflating_stock()
    {
        var (_, sale, _) = await ReserveAsync("RES-08");
        await provider.GetRequiredService<IReturnService>().CancelSaleAsync(sale.Number, "Cliente s'est ravisée", ManagerPin);

        var article = await ReloadAsync("RES-08");
        Assert.Equal(3, article.QuantityOnHand);   // et surtout pas 4
        Assert.Equal(0, article.QuantityReserved);
        Assert.Equal(3, article.QuantityAvailable);
    }

    [Fact]
    public async Task Reserved_advance_is_not_counted_as_revenue_until_handed_over()
    {
        var (_, _, creditId) = await ReserveAsync("RES-09");
        var reports = provider.GetRequiredService<IReportService>();
        var from = DateTimeOffset.Now.AddDays(-1);
        var to = DateTimeOffset.Now.AddDays(1);

        var before = await reports.DashboardAsync(from, to);
        Assert.Equal(0, before.SalesXof);          // rien n'est vendu tant que rien n'est remis
        Assert.Equal(5_000, before.CollectedXof);  // mais l'acompte est bien encaissé

        await Credits.PayAsync(creditId, 15_000, PaymentMode.Cash, null);
        var after = await reports.DashboardAsync(from, to);
        Assert.Equal(20_000, after.SalesXof);
    }

    // --- Meilleur article --------------------------------------------------

    [Fact]
    public async Task Best_seller_groups_variants_under_their_product()
    {
        // Deux variantes du même produit doivent se cumuler, sinon un article décliné en cinq
        // tailles ne remonte jamais devant un article unique.
        var petite = await Catalog.CreateVariantAsync("Robe Amina", "Vêtements", "AMI-S", null, "S", "Rouge", 5_000, 10_000, 10, 0);
        var grande = await Catalog.CreateVariantAsync("Robe Amina", "Vêtements", "AMI-L", null, "L", "Rouge", 5_000, 10_000, 10, 0);
        var autre = await Catalog.CreateVariantAsync("Sac Bina", "Accessoires", "BIN-01", null, null, "Noir", 5_000, 10_000, 10, 0);

        await Sales.CreateAsync(new SaleDraft("top-1", [new SaleLineDraft(petite.Id, 2)], [new PaymentDraft(PaymentMode.Cash, 20_000)]));
        await Sales.CreateAsync(new SaleDraft("top-2", [new SaleLineDraft(grande.Id, 2)], [new PaymentDraft(PaymentMode.Cash, 20_000)]));
        await Sales.CreateAsync(new SaleDraft("top-3", [new SaleLineDraft(autre.Id, 3)], [new PaymentDraft(PaymentMode.Cash, 30_000)]));

        var reports = provider.GetRequiredService<IReportService>();
        var from = DateTimeOffset.Now.AddDays(-1);
        var to = DateTimeOffset.Now.AddDays(1);

        var summary = await reports.DashboardAsync(from, to);
        Assert.NotNull(summary.BestSeller);
        Assert.Equal("Robe Amina", summary.BestSeller.Label);  // 4 pièces cumulées contre 3
        Assert.Equal(4, summary.BestSeller.Quantity);
        Assert.Equal(40_000, summary.BestSeller.ValueXof);

        var byProduct = await reports.TopProductsByProductAsync(from, to);
        Assert.Equal("Robe Amina", byProduct[0].Label);
        Assert.Equal("Sac Bina", byProduct[1].Label);

        // Le classement par variante reste disponible et sépare bien les tailles.
        var byVariant = await reports.TopProductsAsync(from, to);
        Assert.Contains(byVariant, x => x.Label == "AMI-S");
        Assert.Contains(byVariant, x => x.Label == "AMI-L");
    }
}
