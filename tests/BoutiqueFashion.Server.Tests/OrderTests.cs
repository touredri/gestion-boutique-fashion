using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoutiqueFashion.Contracts;
using BoutiqueFashion.Domain;
using BoutiqueFashion.Server.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Server.Tests;

/// <summary>
/// Commandes du site vitrine. Elles sont créées par des visiteuses anonymes : les routes
/// publiques sont donc les seules du produit qu'on ne protège pas, et c'est précisément pour
/// cela qu'elles méritent d'être vérifiées de près.
/// </summary>
public sealed class OrderTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private async Task<Guid> CreateShopAsync(string name)
    {
        var created = await server.Admin.PostAsJsonAsync("/api/shops", new { Name = name });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> PublishArticleAsync(string name, string sku, long price, long? promo = null, Guid? scope = null)
    {
        var categoryId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        (await server.Admin.PutAsJsonAsync("/api/catalog", new CatalogInputDto(
            [new CategoryDto(categoryId, $"Cat {sku}", true)],
            [new ProductDto(productId, categoryId, name, null, null, null, "Femme", null, ProductType.Clothing, true, scope)],
            [new VariantDto(variantId, productId, sku, null, "M", "Rouge", null, null, 5_000, price,
                promo, promo is null ? null : now.AddDays(-1), promo is null ? null : now.AddDays(7), 1, true)])))
            .EnsureSuccessStatusCode();
        return variantId;
    }

    [Fact]
    public async Task The_showcase_is_public_and_hides_the_cost_price()
    {
        await CreateShopAsync("Vitrine Marcory");
        await PublishArticleAsync("Robe vitrine", "VIT-1", 25_000);

        var response = await server.Anonymous().GetAsync("/api/public/showcase");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        // La marge ne regarde pas les visiteuses, et une quantité exacte invite à négocier.
        Assert.DoesNotContain("costXof", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("quantityOnHand", body, StringComparison.OrdinalIgnoreCase);

        var showcase = JsonSerializer.Deserialize<JsonElement>(body);
        Assert.Contains(showcase.GetProperty("items").EnumerateArray(), x => x.GetProperty("name").GetString() == "Robe vitrine");
    }

    [Fact]
    public async Task An_expired_promotion_is_not_advertised()
    {
        var shopId = await CreateShopAsync("Vitrine Promo");
        var categoryId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        (await server.Admin.PutAsJsonAsync("/api/catalog", new CatalogInputDto(
            [new CategoryDto(categoryId, "Promos passées", true)],
            [new ProductDto(productId, categoryId, "Robe soldée hier", null, null, null, null, null, ProductType.Clothing, true, shopId)],
            [new VariantDto(variantId, productId, "PROMO-OLD", null, "M", null, null, null, 5_000, 20_000,
                12_000, DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-2), 1, true)])))
            .EnsureSuccessStatusCode();

        var showcase = await (await server.Anonymous().GetAsync("/api/public/showcase")).Content.ReadFromJsonAsync<JsonElement>();
        var item = showcase.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("variantId").GetGuid() == variantId);

        // Une remise expirée affichée en vitrine est une promesse qu'on ne tiendra pas au comptoir.
        Assert.Equal(JsonValueKind.Null, item.GetProperty("promotionalPriceXof").ValueKind);
    }

    [Fact]
    public async Task A_visitor_can_place_an_order_and_gets_a_reference()
    {
        var shopId = await CreateShopAsync("Commandes Marcory");
        var variantId = await PublishArticleAsync("Robe commandée", "CMD-1", 25_000);

        var response = await server.Anonymous().PostAsJsonAsync("/api/public/orders", new
        {
            ShopId = shopId,
            CustomerName = "Aïcha Koné",
            Phone = "+225 07 00 00 00 33",
            Note = "Je passe samedi matin",
            Lines = new[] { new { VariantId = variantId, Quantity = 2m } },
        });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("CMD-", body.GetProperty("number").GetString());
        Assert.Equal(50_000, body.GetProperty("totalXof").GetInt64());
        Assert.Equal("Commandes Marcory", body.GetProperty("shopName").GetString());
    }

    [Fact]
    public async Task An_order_freezes_the_promotional_price()
    {
        var shopId = await CreateShopAsync("Commandes Promo");
        var variantId = await PublishArticleAsync("Robe en promo", "CMD-PROMO", 30_000, promo: 21_000);

        var response = await server.Anonymous().PostAsJsonAsync("/api/public/orders", new
        {
            ShopId = shopId, CustomerName = "Fanta", Phone = "0700000044",
            Lines = new[] { new { VariantId = variantId, Quantity = 1m } },
        });
        response.EnsureSuccessStatusCode();

        // Une cliente ne doit pas découvrir en boutique que le prix a changé depuis sa réservation.
        Assert.Equal(21_000, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totalXof").GetInt64());
    }

    [Fact]
    public async Task An_order_without_a_phone_number_is_refused()
    {
        var shopId = await CreateShopAsync("Commandes Sans Tel");
        var variantId = await PublishArticleAsync("Article", "CMD-2", 10_000);

        // Sans numéro, personne ne peut rappeler : la commande ne sert à rien.
        var response = await server.Anonymous().PostAsJsonAsync("/api/public/orders", new
        {
            ShopId = shopId, CustomerName = "Anonyme", Phone = "",
            Lines = new[] { new { VariantId = variantId, Quantity = 1m } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_order_for_an_unknown_shop_is_refused()
    {
        var variantId = await PublishArticleAsync("Article", "CMD-3", 10_000);
        var response = await server.Anonymous().PostAsJsonAsync("/api/public/orders", new
        {
            ShopId = Guid.NewGuid(), CustomerName = "Awa", Phone = "0700000055",
            Lines = new[] { new { VariantId = variantId, Quantity = 1m } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Orders_are_listed_and_can_be_rerouted_or_cancelled()
    {
        var marcory = await CreateShopAsync("Routage Marcory");
        var yopougon = await CreateShopAsync("Routage Yopougon");
        var variantId = await PublishArticleAsync("Robe routée", "CMD-4", 15_000);

        (await server.Anonymous().PostAsJsonAsync("/api/public/orders", new
        {
            ShopId = marcory, CustomerName = "Cliente routée", Phone = "0700000066",
            Lines = new[] { new { VariantId = variantId, Quantity = 1m } },
        })).EnsureSuccessStatusCode();

        var listed = await (await server.Admin.GetAsync($"/api/orders?shopId={marcory}")).Content.ReadFromJsonAsync<List<OrderView>>();
        var order = Assert.Single(listed!);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Single(order.Lines);

        (await server.Admin.PostAsJsonAsync($"/api/orders/{order.Id}/reroute", new { ShopId = yopougon })).EnsureSuccessStatusCode();
        Assert.Equal(yopougon, await server.InDbAsync(db => db.Orders.Where(x => x.Id == order.Id).Select(x => x.ShopId).SingleAsync()));

        (await server.Admin.PostAsJsonAsync($"/api/orders/{order.Id}/cancel", new { Reason = "Cliente injoignable" })).EnsureSuccessStatusCode();
        var cancelled = await server.InDbAsync(db => db.Orders.SingleAsync(x => x.Id == order.Id));
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.Equal("Cliente injoignable", cancelled.CancelReason);
    }

    [Fact]
    public async Task Managing_orders_requires_an_account()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Anonymous().GetAsync("/api/orders")).StatusCode);
    }

    [Fact]
    public async Task Cancelling_advances_the_cursor_so_the_till_learns_about_it()
    {
        var shopId = await CreateShopAsync("Curseur Commandes");
        var variantId = await PublishArticleAsync("Article curseur", "CMD-5", 8_000);

        (await server.Anonymous().PostAsJsonAsync("/api/public/orders", new
        {
            ShopId = shopId, CustomerName = "Awa", Phone = "0700000077",
            Lines = new[] { new { VariantId = variantId, Quantity = 1m } },
        })).EnsureSuccessStatusCode();

        var order = await server.InDbAsync(db => db.Orders.SingleAsync(x => x.ShopId == shopId));
        var before = order.Seq;

        (await server.Admin.PostAsJsonAsync($"/api/orders/{order.Id}/cancel", new { Reason = "Doublon" })).EnsureSuccessStatusCode();

        // Sans cette avance, la caisse continuerait de proposer d'encaisser une commande abandonnée.
        var after = await server.InDbAsync(db => db.Orders.Where(x => x.Id == order.Id).Select(x => x.Seq).SingleAsync());
        Assert.True(after > before);
    }
}
