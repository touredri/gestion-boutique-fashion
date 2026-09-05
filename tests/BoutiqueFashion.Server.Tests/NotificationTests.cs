using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BoutiqueFashion.Server.Notifications;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Server.Tests;

public sealed class NotificationTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    [Fact]
    public async Task Settings_round_trip_and_the_number_is_normalised()
    {
        // Un numéro recopié d'un carnet arrive avec un « + », des espaces et des tirets ;
        // OpenWA n'accepte que des chiffres.
        (await server.Admin.PutAsJsonAsync("/api/notifications/settings", new
        {
            WhatsAppNumber = "+225 07 00 00 00 11",
            OnCashOpened = true,
            OnCashClosed = false,
            OnCashVariance = true,
            OnNewOrder = true,
        })).EnsureSuccessStatusCode();

        var settings = await (await server.Admin.GetAsync("/api/notifications/settings")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("2250700000011", settings.GetProperty("whatsAppNumber").GetString());
        Assert.False(settings.GetProperty("onCashClosed").GetBoolean());
        Assert.True(settings.GetProperty("onCashVariance").GetBoolean());
    }

    [Fact]
    public async Task Settings_are_not_public()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.Anonymous().GetAsync("/api/notifications/settings")).StatusCode);
    }

    [Fact]
    public async Task A_browser_subscription_is_stored_once_per_endpoint()
    {
        var endpoint = $"https://push.example.com/{Guid.NewGuid():N}";
        var body = new { Endpoint = endpoint, P256dh = "cle-publique", Auth = "secret", Label = "iPhone" };

        (await server.Admin.PostAsJsonAsync("/api/notifications/subscriptions", body)).EnsureSuccessStatusCode();
        // Réabonner le même navigateur ne doit pas le compter deux fois : il recevrait chaque
        // alerte en double.
        (await server.Admin.PostAsJsonAsync("/api/notifications/subscriptions", body)).EnsureSuccessStatusCode();

        Assert.Equal(1, await server.InDbAsync(db => db.PushSubscriptions.CountAsync(x => x.Endpoint == endpoint)));

        (await server.Admin.DeleteAsync($"/api/notifications/subscriptions?endpoint={Uri.EscapeDataString(endpoint)}")).EnsureSuccessStatusCode();
        Assert.Equal(0, await server.InDbAsync(db => db.PushSubscriptions.CountAsync(x => x.Endpoint == endpoint)));
    }

    [Fact]
    public async Task Sending_a_test_alert_never_fails_when_nothing_is_configured()
    {
        // Ni OpenWA ni clés VAPID dans l'environnement de test : le bouton d'essai doit répondre
        // sans erreur, sinon une alerte non partie ferait tomber l'opération qui l'a déclenchée.
        var response = await server.Admin.PostAsync("/api/notifications/test", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public void Vapid_keys_are_generated_in_the_format_browsers_expect()
    {
        var (publicKey, privateKey) = Vapid.GenerateKeys();

        // Clé publique : point non compressé de 65 octets, encodé en base64url sans remplissage.
        Assert.DoesNotContain('=', publicKey);
        Assert.DoesNotContain('+', publicKey);
        Assert.DoesNotContain('/', publicKey);
        Assert.Equal(65, DecodeLength(publicKey));
        Assert.Equal(32, DecodeLength(privateKey));
    }

    [Fact]
    public void A_vapid_token_is_a_signed_jwt_for_its_audience()
    {
        var (_, privateKey) = Vapid.GenerateKeys();
        var token = Vapid.CreateToken("https://push.example.com", "mailto:a@b.ci", privateKey);

        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);

        var payload = JsonSerializer.Deserialize<JsonElement>(DecodeBytes(parts[1]));
        Assert.Equal("https://push.example.com", payload.GetProperty("aud").GetString());
        // Les services de push rejettent un jeton valable plus de 24 h.
        var lifetime = DateTimeOffset.FromUnixTimeSeconds(payload.GetProperty("exp").GetInt64()) - DateTimeOffset.UtcNow;
        Assert.InRange(lifetime.TotalHours, 1, 24);
    }

    private static int DecodeLength(string value) => DecodeBytes(value).Length;

    private static byte[] DecodeBytes(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
