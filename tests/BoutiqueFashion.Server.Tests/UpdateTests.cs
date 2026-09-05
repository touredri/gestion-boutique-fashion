using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BoutiqueFashion.Contracts;

namespace BoutiqueFashion.Server.Tests;

/// <summary>
/// Le ciblage par boutique est la seule protection contre une version qui démarre mais qui est
/// cassée. S'il fuit, l'échelonnement n'existe plus et les deux boutiques tombent ensemble — ces
/// tests vérifient donc surtout ce qu'un terminal <b>ne voit pas</b>.
/// </summary>
public class UpdateTests(ServerFixture fixture) : IClassFixture<ServerFixture>
{
    private const string AdminKey = ServerFixture.AdminApiKey;

    private HttpClient AsDevice(string token)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient AsPublisher()
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", AdminKey);
        return client;
    }

    private async Task<(Guid ShopId, string Token)> EnrollAsync(string shopName)
    {
        var shop = await fixture.Admin.PostAsJsonAsync("/api/shops", new { Name = shopName, City = "Abidjan" });
        shop.EnsureSuccessStatusCode();
        var shopId = (await shop.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var codeResponse = await fixture.Admin.PostAsync($"/api/shops/{shopId}/enrollment-codes", null);
        codeResponse.EnsureSuccessStatusCode();
        var code = (await codeResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

        var enroll = await fixture.CreateClient().PostAsJsonAsync("/api/devices/enroll", new EnrollRequest(code, "Caisse"));
        enroll.EnsureSuccessStatusCode();
        var response = await enroll.Content.ReadFromJsonAsync<EnrollResponse>();
        return (shopId, response!.DeviceToken);
    }

    /// <summary>Dépose une version : téléverse un faux paquet puis le déclare.</summary>
    private async Task PublishAsync(string version, IReadOnlyList<Guid>? shopIds)
    {
        var publisher = AsPublisher();
        var fileName = $"BanaShop-{version}-full.nupkg";
        var bytes = System.Text.Encoding.UTF8.GetBytes($"paquet factice {version}");

        var upload = await publisher.PutAsync($"/api/releases/files/{fileName}", new ByteArrayContent(bytes));
        upload.EnsureSuccessStatusCode();

        var declare = await publisher.PostAsJsonAsync("/api/releases", new
        {
            Channel = "win",
            Assets = new[]
            {
                new { PackageId = "BanaShop", Version = version, Type = "Full", FileName = fileName, SHA1 = "AA", SHA256 = (string?)null, Size = (long)bytes.Length, NotesMarkdown = (string?)null },
            },
            ShopIds = shopIds,
        });
        declare.EnsureSuccessStatusCode();
    }

    private static async Task<string[]> VersionsAsync(HttpClient device)
    {
        var response = await device.GetAsync("/updates/releases.win.json");
        response.EnsureSuccessStatusCode();
        var feed = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return [.. feed.GetProperty("Assets").EnumerateArray().Select(a => a.GetProperty("Version").GetString()!)];
    }

    [Fact]
    public async Task A_version_targeted_at_one_shop_is_invisible_to_the_other()
    {
        var (cocody, cocodyToken) = await EnrollAsync("Ciblage Cocody");
        var (_, marcoryToken) = await EnrollAsync("Ciblage Marcory");

        await PublishAsync("9.1.0", [cocody]);

        Assert.Contains("9.1.0", await VersionsAsync(AsDevice(cocodyToken)));
        Assert.DoesNotContain("9.1.0", await VersionsAsync(AsDevice(marcoryToken)));
    }

    [Fact]
    public async Task Promoting_reaches_every_shop()
    {
        var (cocody, cocodyToken) = await EnrollAsync("Promo Cocody");
        var (_, marcoryToken) = await EnrollAsync("Promo Marcory");

        await PublishAsync("9.2.0", [cocody]);
        Assert.DoesNotContain("9.2.0", await VersionsAsync(AsDevice(marcoryToken)));

        var promote = await AsPublisher().PostAsJsonAsync("/api/releases/9.2.0/promote", new { Channel = "win", ShopIds = (Guid[]?)null });
        promote.EnsureSuccessStatusCode();

        Assert.Contains("9.2.0", await VersionsAsync(AsDevice(cocodyToken)));
        Assert.Contains("9.2.0", await VersionsAsync(AsDevice(marcoryToken)));
    }

    [Fact]
    public async Task A_withdrawn_version_leaves_the_feed()
    {
        var (_, token) = await EnrollAsync("Retrait");
        await PublishAsync("9.3.0", null);
        Assert.Contains("9.3.0", await VersionsAsync(AsDevice(token)));

        var withdraw = await AsPublisher().PostAsync("/api/releases/9.3.0/withdraw?channel=win", null);
        withdraw.EnsureSuccessStatusCode();

        Assert.DoesNotContain("9.3.0", await VersionsAsync(AsDevice(token)));
    }

    /// <summary>
    /// Le filtrage porte aussi sur le téléchargement. Sans cela, l'échelonnement ne tiendrait
    /// qu'au fait que l'autre terminal ignore le nom du fichier — ce qui n'est pas une sécurité,
    /// c'est une devinette.
    /// </summary>
    [Fact]
    public async Task A_package_cannot_be_downloaded_by_a_shop_it_is_not_meant_for()
    {
        var (cocody, cocodyToken) = await EnrollAsync("Fichier Cocody");
        var (_, marcoryToken) = await EnrollAsync("Fichier Marcory");
        await PublishAsync("9.4.0", [cocody]);

        const string fileName = "BanaShop-9.4.0-full.nupkg";
        Assert.Equal(HttpStatusCode.OK, (await AsDevice(cocodyToken).GetAsync($"/updates/{fileName}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AsDevice(marcoryToken).GetAsync($"/updates/{fileName}")).StatusCode);
    }

    [Fact]
    public async Task The_feed_refuses_an_unknown_device()
    {
        var response = await fixture.CreateClient().GetAsync("/updates/releases.win.json");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Publishing_requires_the_administration_key()
    {
        var response = await fixture.CreateClient().PostAsJsonAsync("/api/releases", new { Channel = "win", Assets = Array.Empty<object>(), ShopIds = (Guid[]?)null });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Un paquet déclaré mais jamais téléversé : le flux le nommerait, et chaque
    /// terminal échouerait à le télécharger sans qu'on sache pourquoi.</summary>
    [Fact]
    public async Task Declaring_a_package_that_was_never_uploaded_is_refused()
    {
        var response = await AsPublisher().PostAsJsonAsync("/api/releases", new
        {
            Channel = "win",
            Assets = new[] { new { PackageId = "BanaShop", Version = "9.9.9", Type = "Full", FileName = "absent-9.9.9-full.nupkg", SHA1 = "AA", SHA256 = (string?)null, Size = 10L, NotesMarkdown = (string?)null } },
            ShopIds = (Guid[]?)null,
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// La taille annoncée est confrontée au fichier réellement présent : c'est ce qui attrape un
    /// transfert coupé, cas bien plus probable qu'une falsification.
    /// </summary>
    [Fact]
    public async Task A_truncated_upload_is_refused()
    {
        var publisher = AsPublisher();
        const string fileName = "BanaShop-9.8.0-full.nupkg";
        var upload = await publisher.PutAsync($"/api/releases/files/{fileName}", new ByteArrayContent([1, 2, 3]));
        upload.EnsureSuccessStatusCode();

        var declare = await publisher.PostAsJsonAsync("/api/releases", new
        {
            Channel = "win",
            Assets = new[] { new { PackageId = "BanaShop", Version = "9.8.0", Type = "Full", FileName = fileName, SHA1 = "AA", SHA256 = (string?)null, Size = 9999L, NotesMarkdown = (string?)null } },
            ShopIds = (Guid[]?)null,
        });
        Assert.Equal(HttpStatusCode.BadRequest, declare.StatusCode);
    }

    [Fact]
    public async Task A_terminal_reports_the_version_it_runs()
    {
        var (shopId, token) = await EnrollAsync("Version remontée");

        var report = await AsDevice(token).PostAsJsonAsync("/api/devices/status",
            new DeviceStatusRequest("1.4.2", "1.4.3", null));
        report.EnsureSuccessStatusCode();

        var devices = await fixture.Admin.GetFromJsonAsync<JsonElement>($"/api/shops/{shopId}/devices");
        var device = devices.EnumerateArray().Single();
        Assert.Equal("1.4.2", device.GetProperty("appVersion").GetString());
        Assert.Equal("1.4.3", device.GetProperty("pendingVersion").GetString());
    }
}
