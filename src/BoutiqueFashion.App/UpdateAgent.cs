// System.IO explicitement : le projet temporaire que WPF fabrique pour compiler le XAML
// n'hérite pas des usings implicites, et File.Create y devient introuvable.
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows.Threading;
using BoutiqueFashion.Application;
using BoutiqueFashion.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Velopack;
using Velopack.Sources;

namespace BoutiqueFashion.App;

/// <summary>
/// Mise à jour du terminal. Voir docs/lot5-mises-a-jour-a-distance.md, §4.
///
/// Deux principes tiennent tout le reste :
///
/// 1. <b>Le téléchargement ne dérange personne</b> — il se fait en tâche de fond, sans question
///    posée. Interrompre une vendeuse pour un transfert de fichier n'a aucun sens.
/// 2. <b>L'installation se fait à la fermeture de l'application</b>, jamais pendant. La boutique
///    ferme le soir, l'échange de fichiers se fait derrière, elle rouvre le lendemain sur la
///    nouvelle version. Aucun écran d'attente devant une cliente.
///
/// Si les conditions ne sont pas réunies à la fermeture — vacation ouverte, file de
/// synchronisation non vide, sauvegarde impossible — on ne fait rien et on retentera demain. Une
/// mise à jour qui attend un jour ne coûte rien ; une mise à jour au mauvais moment coûte une
/// journée de caisse.
/// </summary>
public sealed partial class UpdateAgent : ObservableObject, IDisposable
{
    /// <summary>Six heures : une version n'est jamais urgente, et une boutique n'a pas à devenir
    /// une source de trafic permanente pour un fichier qui ne bouge pas.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IDbContextFactory<BoutiqueDbContext> factory;
    private readonly IUpdateService updates;
    private readonly DispatcherTimer timer;

    private UpdateManager? manager;
    private UpdateInfo? downloaded;

    [ObservableProperty] private string? pendingVersion;
    [ObservableProperty] private string? lastError;

    public UpdateAgent(IDbContextFactory<BoutiqueDbContext> factory, IUpdateService updates)
    {
        this.factory = factory;
        this.updates = updates;
        timer = new DispatcherTimer { Interval = Interval };
        timer.Tick += async (_, _) => await CheckAsync();
    }

    /// <summary>Version en service. Nulle quand l'application n'est pas gérée par Velopack —
    /// exécution depuis bin/, pendant le développement.</summary>
    public string? CurrentVersion => manager?.CurrentVersion?.ToString();

    public bool HasPending => PendingVersion is not null;

    public void Dispose() => timer.Stop();

    public async Task StartAsync()
    {
        // La version en service est notée dès le démarrage, même sans serveur configuré : c'est
        // ce qui permettra plus tard de voir depuis le téléphone qu'une boutique est restée en
        // arrière.
        await updates.RecordAsync(CurrentVersionOrAssembly(), null, null);

        await CheckAsync();
        timer.Start();
    }

    /// <summary>
    /// Cherche, et télécharge si nécessaire. Ne lève jamais : une mise à jour indisponible est un
    /// non-événement, pas une panne.
    /// </summary>
    public async Task CheckAsync()
    {
        try
        {
            var mgr = await ResolveManagerAsync();
            // Pas installée par Velopack : exécution depuis bin/ en développement. Sans cette
            // garde, CheckForUpdatesAsync lève à chaque démarrage en debug.
            if (mgr is null || !mgr.IsInstalled) return;

            var update = await mgr.CheckForUpdatesAsync();
            if (update is null)
            {
                await RecordAsync(null, null);
                return;
            }

            await mgr.DownloadUpdatesAsync(update);
            downloaded = update;
            await RecordAsync(update.TargetFullRelease.Version.ToString(), null);
        }
        catch (Exception e)
        {
            // Le message remonte au téléphone. Sans lui, un terminal qui ne se met plus à jour
            // est indiscernable d'un terminal déjà à jour.
            await RecordAsync(PendingVersion, e.Message);
        }
    }

    /// <summary>
    /// Appelé à la fermeture de l'application. Arme l'installation si — et seulement si — les
    /// conditions métier sont réunies. Retourne vrai quand l'installation a été armée.
    /// </summary>
    public async Task<bool> ApplyOnExitAsync()
    {
        if (downloaded is null || manager is null) return false;

        try
        {
            var readiness = await updates.PrepareAsync();
            if (!readiness.CanApply)
            {
                await RecordAsync(PendingVersion, $"Reporté : {readiness.Reason}");
                return false;
            }

            // WaitExitThenApplyUpdates ne relance pas : Velopack attend que ce processus se
            // termine, échange les fichiers, et s'arrête là. La caisse rouvre le lendemain sur
            // la nouvelle version, sans que personne n'ait rien vu.
            manager.WaitExitThenApplyUpdates(downloaded, silent: true, restart: false);
            return true;
        }
        catch (Exception e)
        {
            await RecordAsync(PendingVersion, e.Message);
            return false;
        }
    }

    /// <summary>
    /// Le gestionnaire n'existe qu'une fois le terminal appairé : l'adresse du serveur et le
    /// jeton d'appareil viennent de l'appairage. Il est donc résolu tardivement, et rerésolu tant
    /// qu'il manque — un terminal appairé en cours de journée n'a pas à être redémarré.
    /// </summary>
    private async Task<UpdateManager?> ResolveManagerAsync()
    {
        if (manager is not null) return manager;

        await using var db = await factory.CreateDbContextAsync();
        var keys = new[] { SyncService.ServerUrlKey, SyncService.TokenKey };
        var settings = await db.AppSettings.AsNoTracking().Where(x => keys.Contains(x.Key))
            .ToDictionaryAsync(x => x.Key, x => x.Value);

        if (!settings.TryGetValue(SyncService.ServerUrlKey, out var baseUrl) || string.IsNullOrEmpty(baseUrl)) return null;
        if (!settings.TryGetValue(SyncService.TokenKey, out var token) || string.IsNullOrEmpty(token)) return null;

        var source = new SimpleWebSource($"{baseUrl.TrimEnd('/')}/updates", new BearerDownloader(token));
        return manager = new UpdateManager(source);
    }

    private async Task RecordAsync(string? pending, string? error)
    {
        PendingVersion = pending;
        LastError = error;
        OnPropertyChanged(nameof(HasPending));
        await updates.RecordAsync(CurrentVersionOrAssembly(), pending, error);
    }

    /// <summary>Version Velopack quand elle existe, sinon celle de l'assembly — qui vaut
    /// « 0.0.0-local » hors publication, et c'est exactement l'information voulue.</summary>
    private string? CurrentVersionOrAssembly() =>
        CurrentVersion ?? typeof(UpdateAgent).Assembly.GetName().Version?.ToString();

    /// <summary>
    /// Téléchargeur qui présente le jeton d'appareil. C'est lui qui permet au serveur de savoir
    /// à quelle boutique il parle, et donc de ne proposer que les versions destinées à celle-ci :
    /// l'échelonnement n'est pas une règle embarquée dans le terminal, c'est une conséquence de
    /// qui pose la question.
    /// </summary>
    private sealed class BearerDownloader(string token) : IFileDownloader
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

        public async Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
        {
            using var response = await SendAsync(url, headers, HttpCompletionOption.ResponseHeadersRead, cancelToken);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync(cancelToken);
            await using var output = File.Create(targetFile);

            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancelToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancelToken);
                copied += read;
                // La progression n'est affichée nulle part aujourd'hui, mais Velopack la lit.
                if (total > 0) progress((int)(copied * 100 / total));
            }
            progress(100);
        }

        public async Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            using var response = await SendAsync(url, headers, HttpCompletionOption.ResponseContentRead, CancellationToken.None);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync();
        }

        public async Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        {
            using var response = await SendAsync(url, headers, HttpCompletionOption.ResponseContentRead, CancellationToken.None);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private Task<HttpResponseMessage> SendAsync(
            string url, IDictionary<string, string>? headers, HttpCompletionOption completion, CancellationToken cancelToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            // Les en-têtes de Velopack d'abord, le nôtre ensuite : il ne doit jamais se faire
            // écraser, sans lui le serveur ignore à quelle boutique il parle.
            foreach (var header in headers ?? new Dictionary<string, string>())
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return Http.SendAsync(request, completion, cancelToken);
        }
    }
}
