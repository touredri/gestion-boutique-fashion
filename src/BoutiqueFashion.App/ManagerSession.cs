using System.Windows.Threading;
using BoutiqueFashion.Application;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BoutiqueFashion.App;

/// <summary>
/// Session « gérant ». Remplace les champs PIN dispersés dans les écrans : le code est saisi une
/// seule fois, conservé en mémoire le temps de la session, puis transmis aux services métier —
/// qui continuent donc de journaliser chaque action sensible dans l'audit.
/// </summary>
public sealed partial class ManagerSession : ObservableObject
{
    /// <summary>Une caisse laissée sans surveillance ne doit pas rester déverrouillée.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);

    private readonly IAuthorizationService authorization;
    private readonly DispatcherTimer idleTimer;

    [ObservableProperty] private bool isManager;
    [ObservableProperty] private bool isPinConfigured;

    public ManagerSession(IAuthorizationService authorization)
    {
        this.authorization = authorization;
        idleTimer = new DispatcherTimer { Interval = IdleTimeout };
        idleTimer.Tick += (_, _) => Lock();
    }

    /// <summary>PIN validé, transmis aux services métier. Vide hors mode gérant.</summary>
    public string Pin { get; private set; } = string.Empty;

    public bool IsSeller => !IsManager;

    partial void OnIsManagerChanged(bool value) => OnPropertyChanged(nameof(IsSeller));

    public async Task RefreshPinStateAsync() => IsPinConfigured = await authorization.IsConfiguredAsync();

    /// <summary>Premier lancement : aucun PIN n'existe en base, il faut le créer avant tout déverrouillage.</summary>
    public async Task CreatePinAsync(string pin, string confirmation)
    {
        if (pin != confirmation) throw new ArgumentException("Les deux codes saisis ne sont pas identiques.");
        await authorization.ConfigurePinAsync(pin);
        IsPinConfigured = true;
        Grant(pin);
    }

    public async Task<bool> TryUnlockAsync(string pin)
    {
        if (string.IsNullOrWhiteSpace(pin)) return false;
        if (!await authorization.AuthorizeSensitiveActionAsync(pin, "Ouvrir le mode gérant")) return false;
        Grant(pin);
        return true;
    }

    /// <summary>À appeler après un changement de PIN pour que la session continue avec le nouveau code.</summary>
    public void UpdatePin(string newPin)
    {
        if (IsManager) Pin = newPin;
    }

    public void Lock()
    {
        idleTimer.Stop();
        Pin = string.Empty;
        IsManager = false;
    }

    /// <summary>Repousse l'expiration. Appelé à chaque interaction depuis la fenêtre principale.</summary>
    public void Touch()
    {
        if (!IsManager) return;
        idleTimer.Stop();
        idleTimer.Start();
    }

    private void Grant(string pin)
    {
        Pin = pin;
        IsManager = true;
        idleTimer.Stop();
        idleTimer.Start();
    }
}
