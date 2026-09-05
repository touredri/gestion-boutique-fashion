using System.Windows.Threading;
using BoutiqueFashion.Application;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BoutiqueFashion.App;

/// <summary>
/// Déclencheur de la synchronisation et porteur de son état affichable.
///
/// Un simple minuteur, volontairement : la caisse ne doit jamais dépendre du réseau, donc rien
/// n'attend ce cycle et rien n'échoue avec lui. Hors ligne, la file grossit en silence et
/// l'indicateur le dit ; au retour du réseau, le cycle suivant rattrape.
/// </summary>
public sealed partial class SyncAgent : ObservableObject
{
    /// <summary>Assez fréquent pour que la propriétaire voie sa journée en quasi-direct, assez
    /// espacé pour ne pas transformer une boutique en source de trafic permanent.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly ISyncService sync;
    private readonly DispatcherTimer timer;

    [ObservableProperty] private SyncState state = new(false, null, 0, null, null, false);

    public SyncAgent(ISyncService sync)
    {
        this.sync = sync;
        timer = new DispatcherTimer { Interval = Interval };
        timer.Tick += async (_, _) => await RunAsync();
    }

    partial void OnStateChanged(SyncState value)
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(IsEnrolled));
        OnPropertyChanged(nameof(HasTrouble));
    }

    public bool IsEnrolled => State.IsEnrolled;

    /// <summary>Vrai quand la dernière tentative a échoué et que des faits attendent : c'est le
    /// seul cas où l'indicateur doit attirer l'œil.</summary>
    public bool HasTrouble => State.IsEnrolled && State.LastError is not null && State.PendingCount > 0;

    public string Label => State switch
    {
        { IsEnrolled: false } => "Terminal non appairé",
        { IsRunning: true } => "Synchronisation…",
        { LastError: not null, PendingCount: > 0 } => $"Hors ligne · {State.PendingCount} en attente",
        { PendingCount: > 0 } => $"{State.PendingCount} en attente",
        { LastSuccessAt: not null } => $"Synchronisé à {State.LastSuccessAt:HH:mm}",
        _ => "Synchronisé",
    };

    public async Task StartAsync()
    {
        await RefreshAsync();
        timer.Start();
    }

    /// <summary>Relit l'état sans appeler le serveur : sert à rafraîchir le compteur d'attente
    /// après une vente.</summary>
    public async Task RefreshAsync()
    {
        try { State = await sync.GetStateAsync(); }
        catch { /* L'indicateur ne doit jamais faire tomber l'interface. */ }
    }

    public async Task RunAsync()
    {
        try { State = await sync.RunOnceAsync(); }
        catch { /* RunOnceAsync ne lève pas ; ce filet couvre l'inattendu. */ }
    }
}
