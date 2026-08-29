using System.Security.Cryptography;
using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Infrastructure;

public sealed class AuthorizationService(IDbContextFactory<BoutiqueDbContext> factory) : IAuthorizationService
{
    private const string PinKey = "Security.ManagerPin";
    private const int Iterations = 210_000;

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.AppSettings.AnyAsync(x => x.Key == PinKey, cancellationToken);
    }

    public async Task ConfigurePinAsync(string pin, CancellationToken cancellationToken = default)
    {
        ValidatePin(pin);
        var salt = RandomNumberGenerator.GetBytes(24);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var encoded = $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var setting = await db.AppSettings.SingleOrDefaultAsync(x => x.Key == PinKey, cancellationToken);
        if (setting is not null) throw new InvalidOperationException("Le PIN est déjà configuré.");
        db.AppSettings.Add(new AppSetting { Key = PinKey, Value = encoded });
        db.AuditEntries.Add(new AuditEntry { Actor = "Responsable", Action = "Configurer PIN", EntityType = "AppSetting", EntityId = PinKey });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> AuthorizeSensitiveActionAsync(string pin, string action, string actor = "Responsable", CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var encoded = await db.AppSettings.Where(x => x.Key == PinKey).Select(x => x.Value).SingleOrDefaultAsync(cancellationToken);
        var valid = encoded is not null && Verify(pin, encoded);
        db.AuditEntries.Add(new AuditEntry { Actor = valid ? actor : "Inconnu", Action = valid ? $"Autoriser: {action}" : $"Refuser: {action}", EntityType = "Authorization", EntityId = Guid.NewGuid().ToString("N") });
        await db.SaveChangesAsync(cancellationToken);
        return valid;
    }

    private static bool Verify(string pin, string encoded)
    {
        try
        {
            var parts = encoded.Split('.');
            var iterations = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    private static void ValidatePin(string pin)
    {
        if (pin.Length is < 4 or > 12 || pin.Any(c => !char.IsDigit(c)))
            throw new ArgumentException("Le PIN doit contenir entre 4 et 12 chiffres.", nameof(pin));
    }
}

public sealed class AppSettingsService(IDbContextFactory<BoutiqueDbContext> factory) : IAppSettingsService
{
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.AppSettings.Where(x => x.Key == key).Select(x => x.Value).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task SetAsync(string key, string value, string actor = "Responsable", CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var setting = await db.AppSettings.SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        var before = setting?.Value;
        if (setting is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else { setting.Value = value; setting.UpdatedAt = DateTimeOffset.UtcNow; }
        db.AuditEntries.Add(new AuditEntry { Actor = actor, Action = "Modifier paramètre", EntityType = "AppSetting", EntityId = key, BeforeJson = before, AfterJson = value });
        await db.SaveChangesAsync(cancellationToken);
    }
}
