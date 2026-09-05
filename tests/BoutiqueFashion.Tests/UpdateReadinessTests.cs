using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using BoutiqueFashion.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BoutiqueFashion.Tests;

/// <summary>
/// La règle qui protège la caisse : on n'installe pas une mise à jour n'importe quand.
///
/// Ces trois conditions sont la seule chose qui sépare « la boutique rouvre demain sur la
/// nouvelle version » de « la boutique rouvre demain avec une vacation à moitié close ». Elles
/// vivent dans l'Infrastructure et non dans l'application WPF précisément pour être vérifiées ici.
/// </summary>
public sealed class UpdateReadinessTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"boutique-maj-{Guid.NewGuid():N}");
    private ServiceProvider provider = null!;

    public async Task InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddBoutiqueInfrastructure(root);
        provider = services.BuildServiceProvider();
        await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        await provider.GetRequiredService<IAuthorizationService>().ConfigurePinAsync("123456");
    }

    public Task DisposeAsync()
    {
        provider.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task An_open_shift_blocks_the_update()
    {
        await provider.GetRequiredService<ICashSessionService>().OpenAsync(10_000, "Awa", "4321");

        var readiness = await provider.GetRequiredService<IUpdateService>().PrepareAsync();

        Assert.False(readiness.CanApply);
        Assert.Contains("Vacation", readiness.Reason);
    }

    [Fact]
    public async Task A_closed_shift_lets_the_update_through_and_a_backup_is_taken()
    {
        var backups = provider.GetRequiredService<IBackupService>();
        var before = (await backups.ListAsync()).Count;

        var readiness = await provider.GetRequiredService<IUpdateService>().PrepareAsync();

        Assert.True(readiness.CanApply, readiness.Reason);
        // La sauvegarde est le seul retour arrière possible sur les données : Velopack sait
        // revenir au binaire précédent, jamais au schéma ni au contenu.
        Assert.True((await backups.ListAsync()).Count > before);
    }

    /// <summary>
    /// Une vente non encore remontée retient la mise à jour. Les données survivraient de toute
    /// façon — l'outbox est en base, hors du dossier d'installation — mais partir à jour évite
    /// d'avoir à démêler un retard de synchronisation d'un problème de mise à jour, ce qui se
    /// fait très mal à trois heures de route.
    /// </summary>
    [Fact]
    public async Task Unsent_facts_hold_the_update_back()
    {
        await provider.GetRequiredService<ICashSessionService>().OpenAsync(10_000);
        var variant = await provider.GetRequiredService<ICatalogService>()
            .CreateVariantAsync("Foulard", "Accessoires", "FOU-01", null, null, "Bleu", 2_000, 5_000, 3, 0);
        await provider.GetRequiredService<ISaleService>().CreateAsync(new SaleDraft(
            "maj-outbox", [new SaleLineDraft(variant.Id, 1)], [new PaymentDraft(PaymentMode.Cash, 5_000)]));
        await provider.GetRequiredService<ICashSessionService>().CloseAsync(15_000, "123456");

        var readiness = await provider.GetRequiredService<IUpdateService>().PrepareAsync();

        Assert.False(readiness.CanApply);
        Assert.Contains("synchronisation", readiness.Reason);
    }

    [Fact]
    public async Task The_reported_status_survives_a_restart()
    {
        var updates = provider.GetRequiredService<IUpdateService>();
        await updates.RecordAsync("1.4.2", "1.4.3", null);

        var status = await updates.GetStatusAsync();

        Assert.Equal("1.4.2", status.CurrentVersion);
        Assert.Equal("1.4.3", status.PendingVersion);
        Assert.Null(status.LastError);
    }
}
