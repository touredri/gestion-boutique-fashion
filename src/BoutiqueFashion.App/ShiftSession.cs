using System.Windows.Threading;
using BoutiqueFashion.Application;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BoutiqueFashion.App;

/// <summary>
/// Verrouillage de l'espace vendeur après inactivité. Le mode gérant avait déjà son expiration
/// (<see cref="ManagerSession"/>) ; la caisse elle-même n'en avait aucune : un terminal laissé
/// seul restait grand ouvert sur les ventes et le catalogue.
///
/// Le verrouillage est strictement volontaire : il ne s'arme que si la vacation en cours a reçu
/// un code. Sans code, rien ne changerait pour l'utilisateur sinon l'impossibilité de rouvrir son
/// propre écran — un terminal condamné en pleine journée de vente.
/// </summary>
public sealed partial class ShiftSession : ObservableObject
{
    /// <summary>Assez long pour ne pas gêner une vente qui s'éternise, assez court pour qu'une
    /// caisse abandonnée ne reste pas ouverte jusqu'au soir.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    private readonly ICashSessionService cash;
    private readonly IAuthorizationService authorization;
    private readonly DispatcherTimer idleTimer;

    /// <summary>Vrai quand l'écran est verrouillé et attend un code.</summary>
    [ObservableProperty] private bool isLocked;
    /// <summary>Vrai quand une vacation protégée par un code est ouverte.</summary>
    [ObservableProperty] private bool isProtected;
    [ObservableProperty] private string operatorName = string.Empty;
    [ObservableProperty] private string pinEntry = string.Empty;
    [ObservableProperty] private string error = string.Empty;

    public ShiftSession(ICashSessionService cash, IAuthorizationService authorization)
    {
        this.cash = cash;
        this.authorization = authorization;
        idleTimer = new DispatcherTimer { Interval = IdleTimeout };
        idleTimer.Tick += (_, _) => Lock();
    }

    /// <summary>À rappeler après chaque ouverture ou clôture de caisse : c'est ce qui décide si
    /// le verrouillage a lieu d'être.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var open = await cash.GetOpenAsync();
            IsProtected = open?.OperatorPinHash is not null;
            OperatorName = open?.OperatorName ?? string.Empty;
        }
        catch
        {
            // Une caisse illisible ne doit surtout pas verrouiller l'écran.
            IsProtected = false;
        }

        if (!IsProtected)
        {
            idleTimer.Stop();
            IsLocked = false;
            PinEntry = Error = string.Empty;
            return;
        }
        if (!IsLocked) Restart();
    }

    /// <summary>Repousse l'expiration. Appelée à chaque interaction depuis la fenêtre principale.</summary>
    public void Touch()
    {
        if (!IsProtected || IsLocked) return;
        Restart();
    }

    public void Lock()
    {
        if (!IsProtected) return;
        idleTimer.Stop();
        PinEntry = Error = string.Empty;
        IsLocked = true;
    }

    /// <summary>Le code de vacation rouvre l'écran ; le code gérant aussi, sans quoi un vendeur
    /// parti avec son code emporterait la caisse avec lui.</summary>
    public async Task<bool> TryUnlockAsync(string pin)
    {
        if (string.IsNullOrWhiteSpace(pin)) return false;
        var granted = await cash.VerifyShiftPinAsync(pin)
            || await authorization.AuthorizeSensitiveActionAsync(pin, "Déverrouiller la caisse");
        if (!granted) return false;
        IsLocked = false;
        PinEntry = Error = string.Empty;
        Restart();
        return true;
    }

    private void Restart()
    {
        idleTimer.Stop();
        idleTimer.Start();
    }
}
