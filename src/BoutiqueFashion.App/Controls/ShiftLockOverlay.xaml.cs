using System.Windows;
using System.Windows.Controls;

namespace BoutiqueFashion.App.Controls;

/// <summary>
/// Pavé de déverrouillage de la vacation. Il porte son propre clavier plutôt que de réutiliser
/// <see cref="TouchKeypadOverlay"/> : celui-ci se referme d'un geste à côté, ce qui n'aurait
/// aucun sens pour un verrou.
/// </summary>
public partial class ShiftLockOverlay : UserControl
{
    private const int MaxLength = 12;
    private string pin = string.Empty;

    public ShiftLockOverlay()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Reset(); };
    }

    private ShiftSession? Session => DataContext as ShiftSession;

    private void Reset()
    {
        pin = string.Empty;
        Render();
    }

    // Le code ne s'affiche jamais en clair : quelqu'un regarde toujours par-dessus l'épaule
    // d'un vendeur au comptoir.
    private void Render() => PinDisplay.Text = new string('●', pin.Length);

    private void OnDigit(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string digit } || pin.Length >= MaxLength) return;
        pin += digit;
        Render();
        if (Session is not null) Session.Error = string.Empty;
    }

    private void OnBackspace(object sender, RoutedEventArgs e)
    {
        if (pin.Length == 0) return;
        pin = pin[..^1];
        Render();
    }

    private async void OnValidate(object sender, RoutedEventArgs e)
    {
        var session = Session;
        if (session is null || pin.Length == 0) return;

        var attempt = pin;
        pin = string.Empty;
        Render();

        if (!await session.TryUnlockAsync(attempt))
            session.Error = "Code incorrect.";
    }
}
