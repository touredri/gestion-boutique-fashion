using System.Net.Http.Json;
using System.Text.Json;

namespace BoutiqueFashion.Server.Tests;

/// <summary>
/// L'identité d'une boutique doit avoir une seule source.
///
/// Elle en avait deux : le site lisait la colonne <c>Shop.Address</c> et l'application de
/// pilotage éditait le réglage <c>Shop.Address</c>. Corriger son adresse changeait donc le ticket
/// de caisse et pas le site, sans que rien ne le signale — le genre d'écart qu'on ne découvre
/// qu'en lisant une affiche fausse dans sa propre vitrine.
/// </summary>
public class ShowcaseTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private async Task<Guid> CreateShopAsync(string name, string city)
    {
        var response = await fixture.Admin.PostAsJsonAsync("/api/shops", new { Name = name, City = city });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> ShowcaseAsync() =>
        await fixture.CreateClient().GetFromJsonAsync<JsonElement>("/api/public/showcase");

    private JsonElement Shop(JsonElement showcase, Guid id) =>
        showcase.GetProperty("shops").EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == id);

    [Fact]
    public async Task Editing_a_shop_changes_what_the_public_site_shows()
    {
        var id = await CreateShopAsync("Vitrine Banankabougou", "Bamako");

        var edit = await fixture.Admin.PutAsJsonAsync($"/api/shops/{id}", new
        {
            Name = "Bana Shop Banankabougou",
            City = "Bamako",
            Address = "Rue 300, Banankabougou",
            Phone = "+223 70 00 00 00",
            Hours = "Lun–Sam 9h–19h",
        });
        edit.EnsureSuccessStatusCode();

        var shop = Shop(await ShowcaseAsync(), id);
        Assert.Equal("Bana Shop Banankabougou", shop.GetProperty("name").GetString());
        Assert.Equal("Rue 300, Banankabougou", shop.GetProperty("address").GetString());
        Assert.Equal("Lun–Sam 9h–19h", shop.GetProperty("hours").GetString());
    }

    /// <summary>
    /// La même correction doit atteindre le terminal, qui imprime l'adresse sur ses tickets. Elle
    /// y arrive par les réglages de boutique, dont le curseur doit avoir avancé — sans quoi la
    /// synchronisation ne redescendrait jamais la modification.
    /// </summary>
    [Fact]
    public async Task Editing_a_shop_reaches_the_terminal_settings()
    {
        var id = await CreateShopAsync("Vitrine ACI", "Bamako");
        var edit = await fixture.Admin.PutAsJsonAsync($"/api/shops/{id}", new
        {
            Name = "Bana Shop ACI 2000",
            City = "Bamako",
            Address = "ACI 2000, près du monument",
            Phone = "+223 76 11 11 11",
            Hours = (string?)null,
        });
        edit.EnsureSuccessStatusCode();

        var settings = await fixture.Admin.GetFromJsonAsync<JsonElement>($"/api/shops/{id}/settings");
        var byKey = settings.EnumerateArray()
            .ToDictionary(x => x.GetProperty("key").GetString()!, x => x.GetProperty("value").GetString());

        Assert.Equal("ACI 2000, près du monument", byKey["Shop.Address"]);
        Assert.Equal("+223 76 11 11 11", byKey["Shop.Phone"]);
        Assert.Equal("Bana Shop ACI 2000", byKey["Shop.Name"]);
    }

    [Fact]
    public async Task An_unknown_shop_cannot_be_edited()
    {
        var response = await fixture.Admin.PutAsJsonAsync($"/api/shops/{Guid.NewGuid()}", new { Name = "Fantôme" });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Les textes du site se règlent depuis l'application. Une année d'ouverture ou une accroche
    /// n'ont aucune raison d'exiger une mise en production.
    /// </summary>
    [Fact]
    public async Task Site_settings_reach_the_public_payload()
    {
        var save = await fixture.Admin.PutAsJsonAsync("/api/site-settings", new[]
        {
            new { Key = "Vitrine.Depuis", Value = "2019" },
            new { Key = "Vitrine.Accroche", Value = "Choisies une par une." },
        });
        save.EnsureSuccessStatusCode();

        var settings = (await ShowcaseAsync()).GetProperty("settings");
        Assert.Equal("2019", settings.GetProperty("Vitrine.Depuis").GetString());
        Assert.Equal("Choisies une par une.", settings.GetProperty("Vitrine.Accroche").GetString());

        // Deuxième écriture sur la même clé : elle se remplace, elle ne s'ajoute pas.
        var again = await fixture.Admin.PutAsJsonAsync("/api/site-settings", new[]
        {
            new { Key = "Vitrine.Depuis", Value = "2020" },
        });
        again.EnsureSuccessStatusCode();
        Assert.Equal("2020", (await ShowcaseAsync()).GetProperty("settings").GetProperty("Vitrine.Depuis").GetString());
    }
}
