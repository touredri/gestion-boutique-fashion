using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoutiqueFashion.Contracts;
using BoutiqueFashion.Domain;

namespace BoutiqueFashion.Server.Tests;

public sealed class AuthAndCatalogScopeTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private async Task<(Guid ShopId, string Token)> EnrollAsync(string shopName)
    {
        var created = await server.Admin.PostAsJsonAsync("/api/shops", new { Name = shopName });
        created.EnsureSuccessStatusCode();
        var shopId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var codeResponse = await server.Admin.PostAsync($"/api/shops/{shopId}/enrollment-codes", null);
        var code = (await codeResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        var enrolled = await server.Anonymous().PostAsJsonAsync("/api/devices/enroll", new EnrollRequest(code, "Terminal"));
        enrolled.EnsureSuccessStatusCode();
        return (shopId, (await enrolled.Content.ReadFromJsonAsync<EnrollResponse>())!.DeviceToken);
    }

    private static async Task<SyncPullResponse> PullAsync(HttpClient device, long since = 0) =>
        (await (await device.GetAsync($"/api/sync/pull?since={since}")).Content.ReadFromJsonAsync<SyncPullResponse>())!;

    // --- Authentification --------------------------------------------------

    [Fact]
    public async Task The_first_account_is_created_from_the_configuration()
    {
        var response = await server.Admin.GetAsync("/api/auth/me");
        response.EnsureSuccessStatusCode();
        Assert.Equal(ServerFixture.OwnerUsername, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("username").GetString());
    }

    [Fact]
    public async Task Piloting_routes_refuse_an_anonymous_caller()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Anonymous().GetAsync("/api/shops")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Anonymous().PostAsJsonAsync("/api/shops", new { Name = "Pirate" })).StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        Assert.Null(await server.SignInAsync(ServerFixture.OwnerUsername, "mauvais-mot-de-passe"));
        Assert.Null(await server.SignInAsync("inconnu", ServerFixture.OwnerPassword));
    }

    [Fact]
    public async Task The_username_is_case_insensitive()
    {
        // « Awa » et « awa » doivent désigner le même compte, sinon deux comptes voisins
        // finiraient par coexister sans qu'on le voie.
        var client = await server.SignInAsync(ServerFixture.OwnerUsername.ToUpperInvariant(), ServerFixture.OwnerPassword);
        Assert.NotNull(client);
        client.Dispose();
    }

    [Fact]
    public async Task A_device_token_does_not_open_the_piloting_routes()
    {
        // Les deux mondes utilisent Bearer : il faut vérifier qu'ils ne se confondent pas.
        var (_, deviceToken) = await EnrollAsync("Boutique Cloison");
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.AsDevice(deviceToken).GetAsync("/api/shops")).StatusCode);
    }

    [Fact]
    public async Task A_user_token_does_not_open_the_sync_routes()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Admin.PostAsJsonAsync("/api/sync/push", new SyncPushRequest([]))).StatusCode);
    }

    [Fact]
    public async Task Logging_out_kills_the_session_immediately()
    {
        var client = (await server.SignInAsync(ServerFixture.OwnerUsername, ServerFixture.OwnerPassword))!;
        (await client.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();

        // C'est la raison du jeton opaque plutôt qu'un JWT : un téléphone perdu se coupe
        // sur-le-champ au lieu d'attendre l'expiration.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        client.Dispose();
    }

    [Fact]
    public async Task Changing_the_password_disconnects_every_session()
    {
        var client = (await server.SignInAsync(ServerFixture.OwnerUsername, ServerFixture.OwnerPassword))!;
        const string newPassword = "nouveau-mot-de-passe-long";

        (await client.PostAsJsonAsync("/api/auth/password", new { CurrentPassword = ServerFixture.OwnerPassword, NewPassword = newPassword })).EnsureSuccessStatusCode();

        // Changer son mot de passe doit déconnecter l'appareil qu'on soupçonne, sinon le geste
        // ne sert à rien.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Null(await server.SignInAsync(ServerFixture.OwnerUsername, ServerFixture.OwnerPassword));

        var renewed = await server.SignInAsync(ServerFixture.OwnerUsername, newPassword);
        Assert.NotNull(renewed);

        // Remise en état : la fixture est partagée par la classe.
        (await renewed.PostAsJsonAsync("/api/auth/password", new { CurrentPassword = newPassword, NewPassword = ServerFixture.OwnerPassword })).EnsureSuccessStatusCode();
        renewed.Dispose();
        client.Dispose();
        server.Admin = (await server.SignInAsync(ServerFixture.OwnerUsername, ServerFixture.OwnerPassword))!;
    }

    [Fact]
    public async Task A_short_password_is_refused()
    {
        var response = await server.Admin.PostAsJsonAsync("/api/auth/password", new { CurrentPassword = ServerFixture.OwnerPassword, NewPassword = "court" });
        Assert.NotEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    // --- Portée du catalogue -----------------------------------------------

    [Fact]
    public async Task A_global_article_reaches_every_shop_and_a_scoped_one_only_its_own()
    {
        var (marcory, tokenMarcory) = await EnrollAsync("Portée Marcory");
        var (_, tokenYopougon) = await EnrollAsync("Portée Yopougon");

        var categoryId = Guid.NewGuid();
        var globalId = Guid.NewGuid();
        var localId = Guid.NewGuid();

        (await server.Admin.PutAsJsonAsync("/api/catalog", new CatalogInputDto(
            [new CategoryDto(categoryId, "Vêtements", true)],
            [
                new ProductDto(globalId, categoryId, "Robe commune", null, null, null, null, null, ProductType.Clothing, true),
                new ProductDto(localId, categoryId, "Pièce exclusive Marcory", null, null, null, null, null, ProductType.Clothing, true, marcory),
            ],
            [
                new VariantDto(Guid.NewGuid(), globalId, "GLOB-1", null, "M", null, null, null, 5_000, 12_000, null, null, null, 1, true),
                new VariantDto(Guid.NewGuid(), localId, "LOCAL-1", null, "M", null, null, null, 5_000, 18_000, null, null, null, 1, true),
            ]))).EnsureSuccessStatusCode();

        var forMarcory = await PullAsync(server.AsDevice(tokenMarcory));
        Assert.Equal(2, forMarcory.Products.Count);
        Assert.Equal(2, forMarcory.Variants.Count);

        // Yopougon ne doit voir ni l'article exclusif ni ses variantes : une pièce qu'on n'y vend
        // pas n'a rien à faire dans sa caisse.
        var forYopougon = await PullAsync(server.AsDevice(tokenYopougon));
        Assert.Equal("Robe commune", Assert.Single(forYopougon.Products).Name);
        Assert.Equal("GLOB-1", Assert.Single(forYopougon.Variants).Sku);
    }

    [Fact]
    public async Task Narrowing_the_scope_retires_the_article_from_the_other_shop()
    {
        var (marcory, tokenMarcory) = await EnrollAsync("Retrait Marcory");
        var (_, tokenYopougon) = await EnrollAsync("Retrait Yopougon");
        var categoryId = Guid.NewGuid();
        var productId = Guid.NewGuid();

        ProductDto Product(Guid? scope) => new(productId, categoryId, "Article mobile", null, null, null, null, null, ProductType.Clothing, true, scope);

        (await server.Admin.PutAsJsonAsync("/api/catalog", new CatalogInputDto(
            [new CategoryDto(categoryId, "Vêtements", true)], [Product(null)], null))).EnsureSuccessStatusCode();

        var yopougon = server.AsDevice(tokenYopougon);
        var initial = await PullAsync(yopougon);
        Assert.Single(initial.Products);

        // L'article devient exclusif à Marcory.
        (await server.Admin.PutAsJsonAsync("/api/catalog", new CatalogInputDto(null, [Product(marcory)], null))).EnsureSuccessStatusCode();

        // Le filtre de descente cesse de l'envoyer à Yopougon : sans la liste des retraits, elle
        // en garderait une copie fantôme, vendable et invisible du serveur.
        var after = await PullAsync(yopougon, initial.Cursor);
        Assert.Empty(after.Products);
        Assert.Equal(productId, Assert.Single(after.RetiredProductIds!));

        // Marcory, elle, reçoit simplement la mise à jour.
        var forMarcory = await PullAsync(server.AsDevice(tokenMarcory), initial.Cursor);
        Assert.Equal(productId, Assert.Single(forMarcory.Products).Id);
        Assert.Empty(forMarcory.RetiredProductIds!);
    }

    [Fact]
    public async Task An_article_scoped_to_an_unknown_shop_is_refused()
    {
        var categoryId = Guid.NewGuid();
        var response = await server.Admin.PutAsJsonAsync("/api/catalog", new CatalogInputDto(
            [new CategoryDto(categoryId, "Vêtements", true)],
            [new ProductDto(Guid.NewGuid(), categoryId, "Orphelin", null, null, null, null, null, ProductType.Clothing, true, Guid.NewGuid())],
            null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
