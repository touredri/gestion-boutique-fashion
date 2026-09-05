using System.Net.Http.Json;
using System.Text.Json;
using BoutiqueFashion.Contracts;
using BoutiqueFashion.Domain;
using BoutiqueFashion.Server.Endpoints;

namespace BoutiqueFashion.Server.Tests;

/// <summary>
/// Lectures de pilotage : ce que la propriétaire verra sur son téléphone. Les chiffres sont
/// vérifiés à partir d'événements poussés comme le ferait un vrai terminal, et non écrits
/// directement en base — c'est le trajet complet qu'on veut valider.
/// </summary>
public sealed class ReportingTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static SyncEvent Event<T>(string type, Guid entityId, T payload) =>
        new(Guid.NewGuid(), type, entityId, DateTimeOffset.UtcNow, JsonSerializer.Serialize(payload, Json));

    private async Task<(Guid ShopId, HttpClient Device)> EnrollAsync(string shopName)
    {
        var created = await server.Admin.PostAsJsonAsync("/api/shops", new { Name = shopName });
        created.EnsureSuccessStatusCode();
        var shopId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var codeResponse = await server.Admin.PostAsync($"/api/shops/{shopId}/enrollment-codes", null);
        var code = (await codeResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        var enrolled = await server.Anonymous().PostAsJsonAsync("/api/devices/enroll", new EnrollRequest(code, "Terminal"));
        var token = (await enrolled.Content.ReadFromJsonAsync<EnrollResponse>())!.DeviceToken;
        return (shopId, server.AsDevice(token));
    }

    /// <summary>Publie un article global et renvoie l'identifiant de sa variante.</summary>
    private async Task<Guid> PublishArticleAsync(string productName, string sku, long cost, long price)
    {
        var categoryId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        (await server.Admin.PutAsJsonAsync("/api/catalog", new CatalogInputDto(
            [new CategoryDto(categoryId, $"Cat {sku}", true)],
            [new ProductDto(productId, categoryId, productName, null, null, null, null, null, ProductType.Clothing, true)],
            [new VariantDto(variantId, productId, sku, null, "M", null, null, null, cost, price, null, null, null, 2, true)])))
            .EnsureSuccessStatusCode();
        return variantId;
    }

    private static SyncEvent Sale(Guid variantId, string number, long price, long cost, decimal quantity, string seller, SaleStatus status = SaleStatus.Completed)
    {
        var saleId = Guid.NewGuid();
        var total = (long)(price * quantity);
        return Event(SyncEntityTypes.Sale, saleId, new SalePayload(
            saleId, number, Guid.NewGuid().ToString("N"), null, null, seller,
            total, 0, total, 0, status, DateTimeOffset.UtcNow,
            [new SaleLineDto(variantId, "SKU", "Article", quantity, price, cost, 0, total)],
            [new PaymentDto(Guid.NewGuid(), PaymentMode.Cash, total, null, false)],
            null));
    }

    [Fact]
    public async Task The_overview_reports_each_shop_separately()
    {
        var (marcory, deviceMarcory) = await EnrollAsync("Aperçu Marcory");
        var (_, deviceYopougon) = await EnrollAsync("Aperçu Yopougon");
        var variantId = await PublishArticleAsync("Robe aperçu", "APER-1", 5_000, 12_000);
        var sessionId = Guid.NewGuid();

        (await deviceMarcory.PostAsJsonAsync("/api/sync/push", new SyncPushRequest([
            Event(SyncEntityTypes.CashSessionOpened, sessionId, new CashSessionOpenedPayload(sessionId, "CAI-1", "Awa", 10_000, DateTimeOffset.UtcNow)),
            Sale(variantId, "APER-M-1", 12_000, 5_000, 2, "Awa"),
        ]))).EnsureSuccessStatusCode();

        (await deviceYopougon.PostAsJsonAsync("/api/sync/push", new SyncPushRequest([
            Sale(variantId, "APER-Y-1", 12_000, 5_000, 1, "Fanta"),
        ]))).EnsureSuccessStatusCode();

        var overview = (await (await server.Admin.GetAsync("/api/overview")).Content.ReadFromJsonAsync<Overview>())!;
        var shop = overview.Shops.Single(x => x.ShopId == marcory);

        Assert.True(shop.IsCashOpen);
        Assert.Equal("Awa", shop.OperatorName);
        Assert.Equal(24_000, shop.SalesXof);
        Assert.Equal(1, shop.SalesCount);
        // Le terminal vient de synchroniser : la propriétaire doit pouvoir voir qu'il est vivant.
        Assert.NotNull(shop.LastSeenAt);

        var other = overview.Shops.Single(x => x.Name == "Aperçu Yopougon");
        Assert.False(other.IsCashOpen);
        Assert.Equal(12_000, other.SalesXof);
    }

    [Fact]
    public async Task The_report_separates_margin_from_profit()
    {
        var (shopId, device) = await EnrollAsync("Rapport Marge");
        var variantId = await PublishArticleAsync("Sac marge", "MARG-1", 4_000, 10_000);

        (await device.PostAsJsonAsync("/api/sync/push", new SyncPushRequest([
            Sale(variantId, "MARG-V1", 10_000, 4_000, 3, "Awa"),
            Event(SyncEntityTypes.Expense, Guid.NewGuid(), new ExpensePayload(Guid.NewGuid(), "Transport", "Taxi", 5_000, PaymentMode.Cash, DateTimeOffset.UtcNow)),
        ]))).EnsureSuccessStatusCode();

        // Borné à cette boutique : la classe partage une base, et un total global additionnerait
        // les ventes des autres tests.
        var report = (await (await server.Admin.GetAsync($"/api/reports?shopId={shopId}")).Content.ReadFromJsonAsync<ReportSummary>())!;

        Assert.Equal(30_000, report.SalesXof);
        Assert.Equal(12_000, report.CostXof);
        Assert.Equal(18_000, report.GrossMarginXof);
        Assert.Equal(5_000, report.ExpensesXof);
        Assert.Equal(13_000, report.EstimatedProfitXof); // marge moins dépenses
        Assert.False(report.CostWarning);
    }

    [Fact]
    public async Task A_missing_cost_price_is_flagged_rather_than_flattering_the_profit()
    {
        var (_, device) = await EnrollAsync("Rapport Coût");
        var variantId = await PublishArticleAsync("Article sans coût", "COUT-1", 0, 9_000);

        (await device.PostAsJsonAsync("/api/sync/push", new SyncPushRequest([
            Sale(variantId, "COUT-V1", 9_000, 0, 1, "Awa"),
        ]))).EnsureSuccessStatusCode();

        var report = (await (await server.Admin.GetAsync($"/api/reports")).Content.ReadFromJsonAsync<ReportSummary>())!;
        // Sans coût de revient la marge vaut le chiffre d'affaires : le dire vaut mieux
        // qu'afficher un bénéfice flatteur et faux.
        Assert.True(report.CostWarning);
    }

    [Fact]
    public async Task A_reserved_advance_is_not_revenue_yet()
    {
        var (shopId, device) = await EnrollAsync("Rapport Avance");
        var variantId = await PublishArticleAsync("Robe réservée", "AVAN-1", 6_000, 20_000);

        (await device.PostAsJsonAsync("/api/sync/push", new SyncPushRequest([
            Sale(variantId, "AVAN-V1", 20_000, 6_000, 1, "Awa", SaleStatus.Reserved),
        ]))).EnsureSuccessStatusCode();

        var report = (await (await server.Admin.GetAsync($"/api/reports?shopId={shopId}")).Content.ReadFromJsonAsync<ReportSummary>())!;
        // La marchandise n'a pas quitté la boutique : ce n'est pas encore un revenu.
        Assert.Equal(0, report.SalesXof);

        var overview = (await (await server.Admin.GetAsync("/api/overview")).Content.ReadFromJsonAsync<Overview>())!;
        Assert.Equal(1, overview.Shops.Single(x => x.ShopId == shopId).ReservedAdvances);
    }

    [Fact]
    public async Task Sales_are_broken_down_by_shop_and_by_operator()
    {
        var (marcory, deviceMarcory) = await EnrollAsync("Détail Marcory");
        var (_, deviceYopougon) = await EnrollAsync("Détail Yopougon");
        var variantId = await PublishArticleAsync("Chemise détail", "DET-1", 3_000, 9_000);

        (await deviceMarcory.PostAsJsonAsync("/api/sync/push", new SyncPushRequest([Sale(variantId, "DET-M1", 9_000, 3_000, 2, "Awa Détail")]))).EnsureSuccessStatusCode();
        (await deviceYopougon.PostAsJsonAsync("/api/sync/push", new SyncPushRequest([Sale(variantId, "DET-Y1", 9_000, 3_000, 1, "Fanta Détail")]))).EnsureSuccessStatusCode();

        var report = (await (await server.Admin.GetAsync("/api/reports")).Content.ReadFromJsonAsync<ReportSummary>())!;
        Assert.Equal(18_000, report.ByShop.Single(x => x.Label == "Détail Marcory").ValueXof);
        Assert.Equal(9_000, report.ByShop.Single(x => x.Label == "Détail Yopougon").ValueXof);
        Assert.Equal(18_000, report.ByOperator.Single(x => x.Label == "Awa Détail").ValueXof);
        Assert.Contains(report.ByPaymentMode, x => x.Label == "Espèces");

        // Filtré sur une boutique, le rapport ne doit plus voir que celle-là.
        var filtered = (await (await server.Admin.GetAsync($"/api/reports?shopId={marcory}")).Content.ReadFromJsonAsync<ReportSummary>())!;
        Assert.Equal(18_000, filtered.SalesXof);
        Assert.Single(filtered.ByShop);
    }

    [Fact]
    public async Task The_best_seller_adds_up_the_variants_of_one_product()
    {
        var (shopId, device) = await EnrollAsync("Meilleur article");
        var categoryId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var small = Guid.NewGuid();
        var large = Guid.NewGuid();
        var other = await PublishArticleAsync("Sac Bina", "BIN-1", 2_000, 8_000);

        (await server.Admin.PutAsJsonAsync("/api/catalog", new CatalogInputDto(
            [new CategoryDto(categoryId, "Robes", true)],
            [new ProductDto(productId, categoryId, "Robe Amina", null, null, null, null, null, ProductType.Clothing, true)],
            [
                new VariantDto(small, productId, "AMI-S", null, "S", null, null, null, 3_000, 10_000, null, null, null, 1, true),
                new VariantDto(large, productId, "AMI-L", null, "L", null, null, null, 3_000, 10_000, null, null, null, 1, true),
            ]))).EnsureSuccessStatusCode();

        (await device.PostAsJsonAsync("/api/sync/push", new SyncPushRequest([
            Sale(small, "TOP-1", 10_000, 3_000, 2, "Awa"),
            Sale(large, "TOP-2", 10_000, 3_000, 2, "Awa"),
            Sale(other, "TOP-3", 8_000, 2_000, 3, "Awa"),
        ]))).EnsureSuccessStatusCode();

        var report = (await (await server.Admin.GetAsync($"/api/reports?shopId={shopId}")).Content.ReadFromJsonAsync<ReportSummary>())!;
        // Quatre pièces cumulées sur deux tailles doivent passer devant trois pièces d'un
        // article unique : compter par SKU les aurait laissées derrière.
        Assert.NotNull(report.BestSeller);
        Assert.Equal("Robe Amina", report.BestSeller.Label);
        Assert.Equal(4, report.BestSeller.Quantity);
    }

    [Fact]
    public async Task Cash_closings_come_back_with_their_variance()
    {
        var (shopId, device) = await EnrollAsync("Clôtures");
        var sessionId = Guid.NewGuid();

        (await device.PostAsJsonAsync("/api/sync/push", new SyncPushRequest([
            Event(SyncEntityTypes.CashSessionOpened, sessionId, new CashSessionOpenedPayload(sessionId, "CAI-CLO", "Awa", 10_000, DateTimeOffset.UtcNow)),
            Event(SyncEntityTypes.CashSessionClosed, sessionId, new CashSessionClosedPayload(
                sessionId, "CAI-CLO", "Awa", "Responsable", 10_000, 42_000, 40_500, -1_500, "billet manquant",
                DateTimeOffset.UtcNow.AddHours(-8), DateTimeOffset.UtcNow)),
        ]))).EnsureSuccessStatusCode();

        var rows = (await (await server.Admin.GetAsync($"/api/cash-closings?shopId={shopId}")).Content.ReadFromJsonAsync<List<CashClosingRow>>())!;
        var closing = Assert.Single(rows);
        Assert.Equal("Clôtures", closing.ShopName);
        Assert.Equal(-1_500, closing.DifferenceXof);
        Assert.Equal("billet manquant", closing.DifferenceReason);
        Assert.Equal("Responsable", closing.ClosedBy);
    }

    [Fact]
    public async Task Low_stock_is_listed_per_shop()
    {
        var (shopId, device) = await EnrollAsync("Stock faible");
        var variantId = await PublishArticleAsync("Article rare", "RARE-1", 1_000, 4_000);

        (await device.PostAsJsonAsync("/api/sync/push", new SyncPushRequest([
            Event(SyncEntityTypes.StockMovement, Guid.NewGuid(),
                new StockMovementPayload(Guid.NewGuid(), variantId, StockMovementType.Receipt, 1, 1_000, "réception", "Test", null, "Awa", DateTimeOffset.UtcNow)),
        ]))).EnsureSuccessStatusCode();

        var rows = (await (await server.Admin.GetAsync($"/api/shops/{shopId}/stock-detail?lowOnly=true")).Content.ReadFromJsonAsync<List<StockRow>>())!;
        var row = Assert.Single(rows);
        Assert.Equal("RARE-1", row.Sku);
        Assert.Equal(1, row.Available);   // 1 en stock pour un seuil de 2

        var overview = (await (await server.Admin.GetAsync("/api/overview")).Content.ReadFromJsonAsync<Overview>())!;
        Assert.Equal(1, overview.Shops.Single(x => x.ShopId == shopId).LowStockCount);
    }

    [Fact]
    public async Task The_catalog_can_be_read_back_for_editing()
    {
        await PublishArticleAsync("Article relu", "RELU-1", 2_000, 7_000);
        var response = await server.Admin.GetAsync("/api/catalog");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(body.GetProperty("variants").EnumerateArray(), x => x.GetProperty("sku").GetString() == "RELU-1");
    }
}
