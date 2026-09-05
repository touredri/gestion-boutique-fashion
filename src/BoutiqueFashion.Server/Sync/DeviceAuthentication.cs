using System.Security.Cryptography;
using System.Text;
using BoutiqueFashion.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Server.Sync;

/// <summary>Boutique à laquelle le terminal appelant est rattaché. Toute lecture et toute
/// écriture en découlent : un terminal ne peut structurellement pas désigner une autre boutique,
/// puisque l'identifiant ne vient jamais de la requête.</summary>
internal sealed record DeviceContext(Guid DeviceId, Guid ShopId, string ShopName);

internal static class DeviceTokens
{
    /// <summary>256 bits d'entropie : le jeton n'est pas devinable, ce qui rend inutile un
    /// hachage lent façon PBKDF2. SHA-256 suffit et reste rapide sur le chemin critique.</summary>
    public static string Create() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Code d'appairage court, recopié à la main sur un écran tactile. L'alphabet exclut
    /// les caractères qui se confondent — 0/O, 1/I/L — parce qu'il sera dicté au téléphone.</summary>
    public static string CreateEnrollmentCode()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return $"BANA-{new string(chars, 0, 4)}-{new string(chars, 4, 4)}";
    }
}

internal static class DeviceAuthentication
{
    public const string Scheme = "Bearer ";

    public static async Task<DeviceContext?> ResolveAsync(HttpContext context, ServerDbContext db, CancellationToken cancellationToken)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith(Scheme, StringComparison.Ordinal)) return null;

        var hash = DeviceTokens.Hash(header[Scheme.Length..].Trim());
        var device = await db.Devices.Include(x => x.Shop)
            .SingleOrDefaultAsync(x => x.TokenHash == hash && x.RevokedAt == null, cancellationToken);
        if (device?.Shop is null || !device.Shop.IsActive) return null;

        // Trace de vie du terminal : c'est ce qui permettra à la propriétaire de voir depuis son
        // téléphone qu'une boutique ne synchronise plus.
        device.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return new DeviceContext(device.Id, device.ShopId, device.Shop.Name);
    }
}

/// <summary>
/// Protection provisoire des routes de pilotage. Une vraie authentification propriétaire
/// arrivera avec la PWA du lot 3 ; d'ici là une clé d'administration en configuration suffit à
/// ne pas laisser la création de boutiques ouverte à tous.
/// </summary>
internal static class AdminAuthentication
{
    public const string HeaderName = "X-Admin-Key";

    public static bool IsAuthorized(HttpContext context, IConfiguration configuration)
    {
        var expected = configuration["Admin:ApiKey"];
        if (string.IsNullOrWhiteSpace(expected)) return false;
        var provided = context.Request.Headers[HeaderName].ToString();
        return !string.IsNullOrEmpty(provided)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
    }
}
