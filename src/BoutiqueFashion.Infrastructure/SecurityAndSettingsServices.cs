using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Infrastructure;

public sealed class AuthorizationService(IDbContextFactory<BoutiqueDbContext> factory) : IAuthorizationService
{
    private const string PinKey = "Security.ManagerPin";

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.AppSettings.AnyAsync(x => x.Key == PinKey, cancellationToken);
    }

    public async Task ConfigurePinAsync(string pin, CancellationToken cancellationToken = default)
    {
        PinHasher.Validate(pin);
        var encoded = PinHasher.Hash(pin);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var setting = await db.AppSettings.SingleOrDefaultAsync(x => x.Key == PinKey, cancellationToken);
        if (setting is not null) throw new InvalidOperationException("Le PIN est déjà configuré.");
        db.AppSettings.Add(new AppSetting { Key = PinKey, Value = encoded });
        db.AuditEntries.Add(new AuditEntry { Actor = "Responsable", Action = "Configurer PIN", EntityType = "AppSetting", EntityId = PinKey });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ChangePinAsync(string oldPin, string newPin, CancellationToken cancellationToken = default)
    {
        PinHasher.Validate(newPin);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var setting = await db.AppSettings.SingleOrDefaultAsync(x => x.Key == PinKey, cancellationToken) ?? throw new InvalidOperationException("Aucun PIN configuré.");
        if (!PinHasher.Verify(oldPin, setting.Value)) throw new UnauthorizedAccessException("Ancien PIN invalide.");
        setting.Value = PinHasher.Hash(newPin);
        setting.UpdatedAt = DateTimeOffset.UtcNow;
        db.AuditEntries.Add(new AuditEntry { Actor = "Responsable", Action = "Changer PIN", EntityType = "AppSetting", EntityId = PinKey });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> AuthorizeSensitiveActionAsync(string pin, string action, string actor = "Responsable", CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var encoded = await db.AppSettings.Where(x => x.Key == PinKey).Select(x => x.Value).SingleOrDefaultAsync(cancellationToken);
        var valid = PinHasher.Verify(pin, encoded);
        db.AuditEntries.Add(new AuditEntry { Actor = valid ? actor : "Inconnu", Action = valid ? $"Autoriser: {action}" : $"Refuser: {action}", EntityType = "Authorization", EntityId = Guid.NewGuid().ToString("N") });
        await db.SaveChangesAsync(cancellationToken);
        return valid;
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
