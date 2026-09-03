using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BoutiqueFashion.App.Controls;

public partial class TouchKeypadOverlay : UserControl
{
    private const int MaxLength = 12;
    private KeypadField? target;
    private string buffer = string.Empty;
    private string original = string.Empty;

    public TouchKeypadOverlay()
    {
        InitializeComponent();
        KeypadHost.Register(this);
    }

    internal void Open(KeypadField field)
    {
        target = field;
        original = field.Value ?? string.Empty;
        buffer = new string(original.Where(char.IsDigit).Take(MaxLength).ToArray());
        // Repli sur le nom accessible pour les champs libellés par un TextBlock voisin plutôt que par Title.
        var title = field.Title;
        if (string.IsNullOrWhiteSpace(title)) title = System.Windows.Automation.AutomationProperties.GetName(field);
        KeypadTitle.Text = string.IsNullOrWhiteSpace(title) ? "Saisie" : title;
        UpdateDisplay();
        Visibility = Visibility.Visible;
        Focus();
    }

    private void UpdateDisplay()
    {
        if (target?.Mask == true)
        {
            KeypadDisplay.Text = buffer.Length == 0 ? "••••" : new string('●', buffer.Length);
            return;
        }
        KeypadDisplay.Text = buffer.Length > 0 && long.TryParse(buffer, out var value) ? value.ToString("N0") : "0";
    }

    private void OnDigit(object sender, RoutedEventArgs e)
    {
        if (buffer.Length < MaxLength && sender is Button { Tag: string digit }) buffer += digit;
        UpdateDisplay();
    }

    private void OnBackspace(object sender, RoutedEventArgs e)
    {
        if (buffer.Length > 0) buffer = buffer[..^1];
        UpdateDisplay();
    }

    // Une seule règle, commune au pavé et au clavier : valider et fermer par le voile conservent la saisie,
    // seul « Annuler » restaure la valeur précédente. Refermer par mégarde ne fait plus disparaître un montant.
    private void OnValidate(object sender, RoutedEventArgs e) => Commit();

    private void OnCancel(object sender, MouseButtonEventArgs e) => Commit();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Revert();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: Commit(); e.Handled = true; return;
            case Key.Escape: Revert(); e.Handled = true; return;
            case Key.Back: OnBackspace(this, new RoutedEventArgs()); e.Handled = true; return;
        }
        if (e.Key is >= Key.D0 and <= Key.D9 && buffer.Length < MaxLength) { buffer += (char)('0' + (e.Key - Key.D0)); UpdateDisplay(); e.Handled = true; return; }
        if (e.Key is >= Key.NumPad0 and <= Key.NumPad9 && buffer.Length < MaxLength) { buffer += (char)('0' + (e.Key - Key.NumPad0)); UpdateDisplay(); e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }

    private void Commit()
    {
        if (target is not null) target.Value = buffer;
        Close();
    }

    private void Revert()
    {
        if (target is not null) target.Value = original;
        Close();
    }

    private void Close()
    {
        var field = target;
        target = null;
        Visibility = Visibility.Collapsed;
        field?.Focus();
    }

    private void OnCardTap(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
