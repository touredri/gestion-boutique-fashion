using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BoutiqueFashion.App.Controls;

public partial class TouchKeypadOverlay : UserControl
{
    private const int MaxLength = 12;
    private KeypadField? target;
    private string buffer = string.Empty;

    public TouchKeypadOverlay()
    {
        InitializeComponent();
        KeypadHost.Register(this);
    }

    internal void Open(KeypadField field)
    {
        target = field;
        buffer = new string((field.Value ?? string.Empty).Where(char.IsDigit).Take(MaxLength).ToArray());
        // Repli sur le nom accessible pour les champs libellés par un TextBlock voisin plutôt que par Title.
        var title = field.Title;
        if (string.IsNullOrWhiteSpace(title)) title = System.Windows.Automation.AutomationProperties.GetName(field);
        KeypadTitle.Text = string.IsNullOrWhiteSpace(title) ? "Saisie" : title;
        UpdateDisplay();
        Visibility = Visibility.Visible;
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

    private void OnValidate(object sender, RoutedEventArgs e)
    {
        if (target is not null) target.Value = buffer;
        Close();
    }

    private void OnCancel(object sender, MouseButtonEventArgs e) => Close();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void Close()
    {
        target = null;
        Visibility = Visibility.Collapsed;
    }

    private void OnCardTap(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
