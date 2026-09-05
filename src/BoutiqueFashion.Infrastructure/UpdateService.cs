using BoutiqueFashion.Application;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Infrastructure;

/// <summary>
/// Décide si une mise à jour peut s'appliquer, et garde trace de ce qui tourne.
///
/// Volontairement ici et non dans l'application WPF : la règle « on n'installe pas caisse
/// ouverte » est du métier, elle doit être testable sans écran. La mécanique Velopack —
/// téléchargement, échange de fichiers, relance — vit dans UpdateAgent, côté interface.
///
/// Voir docs/lot5-mises-a-jour-a-distance.md, §4.
/// </summary>
public sealed class UpdateService(
    IDbContextFactory<BoutiqueDbContext> factory,
    ICashSessionService cashSessions,
    IBackupService backups) : IUpdateService
{
    public const string CurrentVersionKey = "Update.CurrentVersion";
    public const string PendingVersionKey = "Update.PendingVersion";
    public const string LastErrorKey = "Update.LastError";
    public const string LastAppliedAtKey = "Update.LastAppliedAt";

    public async Task<UpdateReadiness> PrepareAsync(CancellationToken cancellationToken = default)
    {
        // 1. Une vacation ouverte. Installer à la fermeture de la fenêtre laisserait une session
        //    ouverte sur une version et close sur une autre — et la vendeuse ne comprendrait pas
        //    pourquoi son attendu de caisse a changé pendant la nuit.
        var open = await cashSessions.GetOpenAsync(cancellationToken);
        if (open is not null)
            return new UpdateReadiness(false, $"Vacation {open.Number} encore ouverte.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // 2. Des faits qui n'ont pas encore quitté le terminal. Ils survivraient — l'outbox est en
        //    base, hors du dossier d'installation — mais partir à jour évite d'avoir à démêler un
        //    retard de synchronisation d'un problème de mise à jour, ce qui se fait mal à distance.
        var pending = await db.SyncOutbox.CountAsync(x => x.SentAt == null, cancellationToken);
        if (pending > 0)
            return new UpdateReadiness(false, $"{pending} élément(s) en attente de synchronisation.");

        // 3. La sauvegarde. Velopack sait revenir au binaire précédent ; rien ne revient sur les
        //    données. C'est le seul filet, il se tend avant, jamais après.
        try
        {
            await backups.CreateAsync(cancellationToken);
        }
        catch (Exception e)
        {
            return new UpdateReadiness(false, $"Sauvegarde impossible : {e.Message}");
        }

        return new UpdateReadiness(true, "Prêt.");
    }

    public async Task<UpdateStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var keys = new[] { CurrentVersionKey, PendingVersionKey, LastErrorKey };
        var rows = await db.AppSettings.AsNoTracking().Where(x => keys.Contains(x.Key))
            .ToDictionaryAsync(x => x.Key, x => x.Value, cancellationToken);
        return new UpdateStatus(
            rows.GetValueOrDefault(CurrentVersionKey),
            rows.GetValueOrDefault(PendingVersionKey),
            rows.GetValueOrDefault(LastErrorKey));
    }

    public async Task RecordAsync(string? currentVersion, string? pendingVersion, string? lastError, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await SetAsync(db, CurrentVersionKey, currentVersion, cancellationToken);
        await SetAsync(db, PendingVersionKey, pendingVersion, cancellationToken);
        await SetAsync(db, LastErrorKey, lastError, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task SetAsync(BoutiqueDbContext db, string key, string? value, CancellationToken cancellationToken)
    {
        var row = await db.AppSettings.SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (row is null)
        {
            // Une valeur absente ne se stocke pas : mieux vaut pas de ligne qu'une ligne vide,
            // que la lecture aurait ensuite à distinguer de « jamais renseigné ».
            if (string.IsNullOrEmpty(value)) return;
            db.AppSettings.Add(new Domain.AppSetting { Key = key, Value = value });
            return;
        }
        row.Value = value ?? string.Empty;
        row.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
