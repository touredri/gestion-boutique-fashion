using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoutiqueFashion.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Server.Notifications;

public enum NotificationKind { CashOpened, CashClosed, CashVariance, NewOrder }

public sealed record Alert(NotificationKind Kind, string Title, string Body);

/// <summary>
/// Envoi des alertes. Deux canaux, aux rôles délibérément différents.
///
/// <b>WhatsApp</b> porte le détail : c'est là que la propriétaire lit réellement ses messages, et
/// c'est le canal qu'elle a demandé. Il passe par OpenWA, hébergé à côté du serveur.
///
/// <b>La notification web</b> ne porte qu'une alerte générique. Y mettre du texte imposerait le
/// chiffrement RFC 8291 — ECDH, HKDF, AES-GCM — dont une erreur silencieuse ne se verrait qu'en
/// production, sur un téléphone, sans trace. Une notification sobre qui ouvre l'application au
/// bon endroit rend le même service pour un risque nul.
/// </summary>
internal sealed class Notifier(ServerDbContext db, IHttpClientFactory clients, IConfiguration configuration, ILogger<Notifier> logger)
{
    public async Task SendAsync(Alert alert, CancellationToken cancellationToken = default)
    {
        var settings = await db.NotificationSettings.FirstOrDefaultAsync(cancellationToken) ?? new Data.NotificationSettings();
        if (!IsEnabled(settings, alert.Kind)) return;

        // Une alerte non partie ne doit jamais faire échouer l'opération qui l'a déclenchée :
        // une vente remontée avec succès reste un succès même si le message ne part pas.
        try { await SendWhatsAppAsync(settings.WhatsAppNumber, alert, cancellationToken); }
        catch (Exception e) { logger.LogWarning(e, "Alerte WhatsApp non envoyée."); }

        try { await SendWebPushAsync(cancellationToken); }
        catch (Exception e) { logger.LogWarning(e, "Notification web non envoyée."); }
    }

    private static bool IsEnabled(Data.NotificationSettings settings, NotificationKind kind) => kind switch
    {
        NotificationKind.CashOpened => settings.OnCashOpened,
        NotificationKind.CashClosed => settings.OnCashClosed,
        NotificationKind.CashVariance => settings.OnCashVariance,
        NotificationKind.NewOrder => settings.OnNewOrder,
        _ => false,
    };

    // --- WhatsApp via OpenWA ------------------------------------------------

    private async Task SendWhatsAppAsync(string? number, Alert alert, CancellationToken cancellationToken)
    {
        var baseUrl = configuration["OpenWa:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(number)) return;

        var client = clients.CreateClient("openwa");
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/sendText")
        {
            // OpenWA attend l'identifiant de discussion, pas un numéro brut.
            Content = JsonContent.Create(new { args = new { to = $"{number}@c.us", content = $"*{alert.Title}*\n{alert.Body}" } }),
        };
        var key = configuration["OpenWa:ApiKey"];
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Add("api_key", key);

        var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            logger.LogWarning("OpenWA a refusé l'envoi ({Status}).", (int)response.StatusCode);
    }

    // --- Notification web ---------------------------------------------------

    private async Task SendWebPushAsync(CancellationToken cancellationToken)
    {
        var publicKey = configuration["Vapid:PublicKey"];
        var privateKey = configuration["Vapid:PrivateKey"];
        var subject = configuration["Vapid:Subject"] ?? "mailto:contact@example.com";
        if (string.IsNullOrWhiteSpace(publicKey) || string.IsNullOrWhiteSpace(privateKey)) return;

        var subscriptions = await db.PushSubscriptions.ToListAsync(cancellationToken);
        if (subscriptions.Count == 0) return;

        var client = clients.CreateClient("webpush");
        foreach (var subscription in subscriptions)
        {
            var origin = new Uri(subscription.Endpoint).GetLeftPart(UriPartial.Authority);
            var request = new HttpRequestMessage(HttpMethod.Post, subscription.Endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", $"vapid t={Vapid.CreateToken(origin, subject, privateKey)}, k={publicKey}");
            request.Headers.TryAddWithoutValidation("TTL", "86400");
            // Corps vide : sans chiffrement, la spécification interdit toute charge utile.
            request.Content = new ByteArrayContent([]);
            request.Content.Headers.ContentLength = 0;

            var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            {
                // Le navigateur a désinstallé l'application ou révoqué l'abonnement : le garder
                // ferait échouer tous les envois suivants.
                db.PushSubscriptions.Remove(subscription);
            }
            else if (response.IsSuccessStatusCode)
            {
                subscription.LastUsedAt = DateTimeOffset.UtcNow;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Jeton VAPID : un JWT ES256 qui prouve au service de push que l'envoi vient bien de ce serveur.
/// Écrit à la main plutôt qu'avec une bibliothèque — c'est une signature et deux encodages, et
/// une dépendance de plus pour cela ne se justifierait pas.
/// </summary>
internal static class Vapid
{
    public static string CreateToken(string audience, string subject, string privateKeyBase64Url)
    {
        var header = Encode("""{"typ":"JWT","alg":"ES256"}"""u8.ToArray());
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            aud = audience,
            // 12 heures : au-delà, les services de push rejettent le jeton.
            exp = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds(),
            sub = subject,
        }));

        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Decode(privateKeyBase64Url),
        });
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256);
        return $"{header}.{payload}.{Encode(signature)}";
    }

    /// <summary>Génère une paire de clés à publier dans la configuration.</summary>
    public static (string PublicKey, string PrivateKey) GenerateKeys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);
        // Format non compressé attendu par la spécification : 0x04 puis X et Y.
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);
        return (Encode(publicKey), Encode(parameters.D!));
    }

    private static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
