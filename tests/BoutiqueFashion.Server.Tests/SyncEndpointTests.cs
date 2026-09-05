using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoutiqueFashion.Contracts;
using BoutiqueFashion.Domain;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Server.Tests;

public sealed class SyncEndpointTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static SyncEvent Event<T>(string type, Guid entityId, T payload) =>
        new(Guid.NewGuid(), type, entityId, DateTimeOffset.UtcNow, JsonSerializer.Serialize(payload, Json));

    private async Task<(Guid ShopId, string Token)> EnrollAsync(string shopName)
    {
        var created = await server.Admin.PostAsJsonAsync("/api/shops", new { Name = shopName, City = "Abidjan" });
        created.EnsureSuccessStatusCode();
        var shopId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var codeResponse = await server.Admin.PostAsync($"/api/shops/{shopId}/enrollment-codes", null);
        codeResponse.EnsureSuccessStatusCode();
        var code = (await codeResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        var enrolled = await server.Anonymous().PostAsJsonAsync("/api/devices/enroll", new EnrollRequest(code, "Terminal test"));
        enrolled.EnsureSuccessStatusCode();
        var body = (await enrolled.Content.ReadFromJsonAsync<EnrollResponse>())!;
        return (body.ShopId, body.DeviceToken);
    }

    private static SalePayload SampleSale(Guid variantId, out Guid saleId, string number = "TIC-0001")
    {
        saleId = Guid.NewGuid();
        return new SalePayload(
            saleId, number, Guid.NewGuid().ToString("N"), null, null, "Awa",
            20_000, 0, 20_000, 0, SaleStatus.Completed, DateTimeOffset.UtcNow,
            [new SaleLineDto(variantId, "SKU-1", "Robe", 1, 20_000, 10_000, 0, 20_000)],
            [new PaymentDto(Guid.NewGuid(), PaymentMode.Cash, 20_000, null, false)],
            null);
    }

    // --- Appairage ---------------------------------------------------------

    [Fact]
    public async Task Health_is_open_to_everyone()
    {
        var response = await server.Anonymous().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Creating_a_shop_requires_the_admin_key()
    {
        var response = await server.Anonymous().PostAsJsonAsync("/api/shops", new { Name = "Pirate" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_enrollment_code_works_once_and_only_once()
    {
        var created = await server.Admin.PostAsJsonAsync("/api/shops", new { Name = "Boutique Cocody" });
        var shopId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var codeResponse = await server.Admin.PostAsync($"/api/shops/{shopId}/enrollment-codes", null);
        var code = (await codeResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        var first = await server.Anonymous().PostAsJsonAsync("/api/devices/enroll", new EnrollRequest(code, "Terminal 1"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Rejouer le code doit échouer : sinon un code intercepté appairerait des terminaux
        // pirates aussi longtemps qu'il reste valide.
        var second = await server.Anonymous().PostAsJsonAsync("/api/devices/enroll", new EnrollRequest(code, "Terminal 2"));
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task An_expired_code_is_refused()
    {
        var (shopId, _) = await EnrollAsync("Boutique Expirée");
        var stale = await server.InDbAsync(async db =>
        {
            var code = new Server.Data.EnrollmentCode
            {
                Code = "BANA-DEAD-BEEF",
                ShopId = shopId,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            };
            db.EnrollmentCodes.Add(code);
            await db.SaveChangesAsync();
            return code.Code;
        });

        var response = await server.Anonymous().PostAsJsonAsync("/api/devices/enroll", new EnrollRequest(stale, "Terminal tardif"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Sync_without_a_device_token_is_refused()
    {
        var response = await server.Anonymous().PostAsJsonAsync("/api/sync/push", new SyncPushRequest([]));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_revoked_device_loses_access()
    {
        var (_, token) = await EnrollAsync("Boutique Révoquée");
        await server.InDbAsync(async db =>
        {
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
            var device = await db.Devices.SingleAsync(x => x.TokenHash == hash);
            device.RevokedAt = DateTimeOffset.UtcNow;
            return await db.SaveChangesAsync();
        });

        var response = await server.AsDevice(token).PostAsJsonAsync("/api/sync/push", new SyncPushRequest([]));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Remontée ----------------------------------------------------------

    [Fact]
    public async Task A_pushed_sale_lands_with_its_lines_and_payments()
    {
        var (shopId, token) = await EnrollAsync("Boutique Vente");
        var payload = SampleSale(Guid.NewGuid(), out var saleId);

        var response = await server.AsDevice(token).PostAsJsonAsync("/api/sync/push",
            new SyncPushRequest([Event(SyncEntityTypes.Sale, saleId, payload)]));
        response.EnsureSuccessStatusCode();

        var result = (await response.Content.ReadFromJsonAsync<SyncPushResponse>())!;
        Assert.Single(result.AcceptedIds);
        Assert.Empty(result.Rejected);

        var stored = await server.InDbAsync(db => db.Sales.Include(x => x.Lines).Include(x => x.Payments).SingleAsync(x => x.Id == saleId));
        Assert.Equal(shopId, stored.ShopId);
        Assert.Equal("Awa", stored.SellerName);
        Assert.Single(stored.Lines);
        Assert.Single(stored.Payments);
    }

    [Fact]
    public async Task Replaying_a_batch_changes_nothing()
    {
        var (_, token) = await EnrollAsync("Boutique Rejeu");
        var payload = SampleSale(Guid.NewGuid(), out var saleId, "TIC-REJEU");
        var batch = new SyncPushRequest([Event(SyncEntityTypes.Sale, saleId, payload)]);
        var device = server.AsDevice(token);

        // Le terminal doit pouvoir réessayer sans jamais se demander si le lot précédent est passé.
        (await device.PostAsJsonAsync("/api/sync/push", batch)).EnsureSuccessStatusCode();
        var second = await device.PostAsJsonAsync("/api/sync/push", batch);
        second.EnsureSuccessStatusCode();

        Assert.Empty((await second.Content.ReadFromJsonAsync<SyncPushResponse>())!.Rejected);
        Assert.Equal(1, await server.InDbAsync(db => db.Sales.CountAsync(x => x.Id == saleId)));
    }

    [Fact]
    public async Task A_bad_event_is_rejected_alone_and_the_batch_goes_through()
    {
        var (_, token) = await EnrollAsync("Boutique Lot");
        var good = SampleSale(Guid.NewGuid(), out var saleId, "TIC-BON");

        var response = await server.AsDevice(token).PostAsJsonAsync("/api/sync/push", new SyncPushRequest([
            new SyncEvent(Guid.NewGuid(), "TypeInconnu", Guid.NewGuid(), DateTimeOffset.UtcNow, "{}"),
            Event(SyncEntityTypes.Sale, saleId, good),
        ]));
        response.EnsureSuccessStatusCode();

        // Sans cette isolation, un seul enregistrement corrompu gèlerait à jamais la remontée.
        var result = (await response.Content.ReadFromJsonAsync<SyncPushResponse>())!;
        Assert.Single(result.Rejected);
        Assert.Single(result.AcceptedIds);
        Assert.Equal(1, await server.InDbAsync(db => db.Sales.CountAsync(x => x.Id == saleId)));
    }

    [Fact]
    public async Task Two_shops_may_share_a_document_number()
    {
        var (_, first) = await EnrollAsync("Boutique Marcory");
        var (_, second) = await EnrollAsync("Boutique Yopougon");

        var a = SampleSale(Guid.NewGuid(), out var saleA, "TIC-0001");
        var b = SampleSale(Guid.NewGuid(), out var saleB, "TIC-0001");

        (await server.AsDevice(first).PostAsJsonAsync("/api/sync/push", new SyncPushRequest([Event(SyncEntityTypes.Sale, saleA, a)]))).EnsureSuccessStatusCode();
        var response = await server.AsDevice(second).PostAsJsonAsync("/api/sync/push", new SyncPushRequest([Event(SyncEntityTypes.Sale, saleB, b)]));
        response.EnsureSuccessStatusCode();

        // Les deux terminaux numérotent chacun depuis 1 : une unicité globale rejetterait à
        // jamais la seconde boutique.
        Assert.Empty((await response.Content.ReadFromJsonAsync<SyncPushResponse>())!.Rejected);
        Assert.Equal(2, await server.InDbAsync(db => db.Sales.CountAsync(x => x.Number == "TIC-0001")));
    }

    [Fact]
    public async Task Stock_is_rebuilt_from_the_movements_of_each_shop()
    {
        var (shopId, token) = await EnrollAsync("Boutique Stock");
        var variantId = Guid.NewGuid();
        var device = server.AsDevice(token);

        SyncEvent Movement(StockMovementType type, decimal delta) =>
            Event(SyncEntityTypes.StockMovement, Guid.NewGuid(),
                new StockMovementPayload(Guid.NewGuid(), variantId, type, delta, 5_000, "test", "Test", null, "Awa", DateTimeOffset.UtcNow));

        var response = await device.PostAsJsonAsync("/api/sync/push", new SyncPushRequest([
            Movement(StockMovementType.Receipt, 10),
            Movement(StockMovementType.Sale, -3),
            Movement(StockMovementType.Reservation, -2),
        ]));
        response.EnsureSuccessStatusCode();

        var stock = await server.InDbAsync(db => db.ShopStocks.SingleAsync(x => x.ShopId == shopId && x.VariantId == variantId));
        Assert.Equal(7, stock.QuantityOnHand);      // la réservation ne sort pas le stock
        Assert.Equal(2, stock.QuantityReserved);
        Assert.Equal(5_000, stock.LastUnitCostXof);

        // Puis la remise : la réservation se lève et la marchandise sort enfin.
        (await device.PostAsJsonAsync("/api/sync/push", new SyncPushRequest([
            Movement(StockMovementType.ReservationRelease, 2),
            Movement(StockMovementType.Sale, -2),
        ]))).EnsureSuccessStatusCode();

        var after = await server.InDbAsync(db => db.ShopStocks.SingleAsync(x => x.ShopId == shopId && x.VariantId == variantId));
        Assert.Equal(5, after.QuantityOnHand);
        Assert.Equal(0, after.QuantityReserved);
    }

    [Fact]
    public async Task A_shop_never_sees_another_shops_stock()
    {
        var (marcory, tokenMarcory) = await EnrollAsync("Marcory");
        var (yopougon, tokenYopougon) = await EnrollAsync("Yopougon");
        var variantId = Guid.NewGuid();

        SyncEvent Receipt(decimal quantity) =>
            Event(SyncEntityTypes.StockMovement, Guid.NewGuid(),
                new StockMovementPayload(Guid.NewGuid(), variantId, StockMovementType.Receipt, quantity, 1_000, "réception", "Test", null, "Awa", DateTimeOffset.UtcNow));

        (await server.AsDevice(tokenMarcory).PostAsJsonAsync("/api/sync/push", new SyncPushRequest([Receipt(10)]))).EnsureSuccessStatusCode();
        (await server.AsDevice(tokenYopougon).PostAsJsonAsync("/api/sync/push", new SyncPushRequest([Receipt(4)]))).EnsureSuccessStatusCode();

        // Le même article, deux boutiques, deux stocks. C'est toute la raison d'être du modèle serveur.
        Assert.Equal(10, (await server.InDbAsync(db => db.ShopStocks.SingleAsync(x => x.ShopId == marcory && x.VariantId == variantId))).QuantityOnHand);
        Assert.Equal(4, (await server.InDbAsync(db => db.ShopStocks.SingleAsync(x => x.ShopId == yopougon && x.VariantId == variantId))).QuantityOnHand);
    }

    [Fact]
    public async Task A_closed_till_carries_its_counted_figures()
    {
        var (_, token) = await EnrollAsync("Boutique Caisse");
        var sessionId = Guid.NewGuid();
        var device = server.AsDevice(token);

        (await device.PostAsJsonAsync("/api/sync/push", new SyncPushRequest([
            Event(SyncEntityTypes.CashSessionOpened, sessionId,
                new CashSessionOpenedPayload(sessionId, "CAI-1", "Awa", 10_000, DateTimeOffset.UtcNow)),
        ]))).EnsureSuccessStatusCode();

        (await device.PostAsJsonAsync("/api/sync/push", new SyncPushRequest([
            Event(SyncEntityTypes.CashSessionClosed, sessionId,
                new CashSessionClosedPayload(sessionId, "CAI-1", "Awa", "Awa", 10_000, 32_000, 31_000, -1_000, "billet manquant", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)),
        ]))).EnsureSuccessStatusCode();

        var session = await server.InDbAsync(db => db.CashSessions.SingleAsync(x => x.Id == sessionId));
        Assert.True(session.IsClosed);
        Assert.Equal(-1_000, session.DifferenceXof);
        Assert.Equal("billet manquant", session.DifferenceReason);
    }

    // --- Descente ----------------------------------------------------------

    [Fact]
    public async Task The_catalog_comes_down_and_the_cursor_stops_replaying_it()
    {
        var (_, token) = await EnrollAsync("Boutique Catalogue");
        var categoryId = Guid.NewGuid();
        var productId = Guid.NewGuid();

        (await server.Admin.PutAsJsonAsync("/api/catalog", new CatalogInputDto(
            [new CategoryDto(categoryId, "Vêtements", true)],
            [new ProductDto(productId, categoryId, "Robe Amina", null, null, null, "Femme", null, ProductType.Clothing, true)],
            [new VariantDto(Guid.NewGuid(), productId, "AMI-M", null, "M", "Rouge", null, null, 10_000, 25_000, null, null, null, 2, true)])))
            .EnsureSuccessStatusCode();

        var device = server.AsDevice(token);
        var first = (await (await device.GetAsync("/api/sync/pull?since=0")).Content.ReadFromJsonAsync<SyncPullResponse>())!;
        Assert.Single(first.Categories);
        Assert.Single(first.Products);
        Assert.Single(first.Variants);
        Assert.Equal("Robe Amina", first.Products[0].Name);
        Assert.True(first.Cursor > 0);

        // Repasser avec le curseur ne doit plus rien rendre : sinon chaque synchronisation
        // retéléchargerait tout le catalogue.
        var second = (await (await device.GetAsync($"/api/sync/pull?since={first.Cursor}")).Content.ReadFromJsonAsync<SyncPullResponse>())!;
        Assert.Empty(second.Categories);
        Assert.Empty(second.Products);
        Assert.Empty(second.Variants);
    }

    [Fact]
    public async Task A_price_change_comes_back_down()
    {
        var (_, token) = await EnrollAsync("Boutique Prix");
        var categoryId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var variantId = Guid.NewGuid();

        VariantDto Variant(long price) => new(variantId, productId, "PRX-1", null, "M", "Bleu", null, null, 5_000, price, null, null, null, 1, true);

        (await server.Admin.PutAsJsonAsync("/api/catalog", new CatalogInputDto(
            [new CategoryDto(categoryId, "Vêtements", true)],
            [new ProductDto(productId, categoryId, "Chemise", null, null, null, null, null, ProductType.Clothing, true)],
            [Variant(12_000)]))).EnsureSuccessStatusCode();

        var device = server.AsDevice(token);
        var initial = (await (await device.GetAsync("/api/sync/pull?since=0")).Content.ReadFromJsonAsync<SyncPullResponse>())!;

        (await server.Admin.PutAsJsonAsync("/api/catalog", new CatalogInputDto(null, null, [Variant(15_000)]))).EnsureSuccessStatusCode();

        var updated = (await (await device.GetAsync($"/api/sync/pull?since={initial.Cursor}")).Content.ReadFromJsonAsync<SyncPullResponse>())!;
        Assert.Equal(15_000, Assert.Single(updated.Variants).PriceXof);
    }

    [Fact]
    public async Task Settings_only_come_down_to_their_own_shop()
    {
        var (marcory, tokenMarcory) = await EnrollAsync("Réglages Marcory");
        var (yopougon, tokenYopougon) = await EnrollAsync("Réglages Yopougon");

        (await server.Admin.PutAsJsonAsync($"/api/shops/{marcory}/settings", new[] { new SettingDto("Shop.Name", "Boutique Marcory") })).EnsureSuccessStatusCode();
        (await server.Admin.PutAsJsonAsync($"/api/shops/{yopougon}/settings", new[] { new SettingDto("Shop.Name", "Boutique Yopougon") })).EnsureSuccessStatusCode();

        var forMarcory = (await (await server.AsDevice(tokenMarcory).GetAsync("/api/sync/pull?since=0")).Content.ReadFromJsonAsync<SyncPullResponse>())!;
        Assert.Equal("Boutique Marcory", Assert.Single(forMarcory.Settings).Value);

        var forYopougon = (await (await server.AsDevice(tokenYopougon).GetAsync("/api/sync/pull?since=0")).Content.ReadFromJsonAsync<SyncPullResponse>())!;
        Assert.Equal("Boutique Yopougon", Assert.Single(forYopougon.Settings).Value);
    }
}

/// <summary>Miroir de l'entrée du serveur, qui est interne à celui-ci.</summary>
public sealed record CatalogInputDto(
    IReadOnlyList<CategoryDto>? Categories,
    IReadOnlyList<ProductDto>? Products,
    IReadOnlyList<VariantDto>? Variants);
