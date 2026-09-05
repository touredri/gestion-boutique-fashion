using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace BoutiqueFashion.App.Controls;

public partial class TouchKeyboardOverlay : UserControl
{
    private static readonly string[][] LetterRows =
    [
        ["a", "z", "e", "r", "t", "y", "u", "i", "o", "p"],
        ["q", "s", "d", "f", "g", "h", "j", "k", "l", "m"],
        ["w", "x", "c", "v", "b", "n", ",", ";", ":", "!"],
        // Sans cette rangée, aucun nom français ou malien ne pouvait être saisi correctement,
        // et la recherche client par nom ne retrouvait donc rien.
        ["é", "è", "ê", "à", "â", "ç", "ù", "î", "ô", "'"]
    ];

    private static readonly string[][] DigitRows =
    [
        ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"],
        ["@", "#", "€", "$", "%", "*", "+", "-", "=", "/"],
        ["(", ")", "_", ".", "?", "'", "\"", "&", "<", ">"],
        ["[", "]", "{", "}", "|", "\\", "~", "^", "°", ";"]
    ];

    private TextBox? target;
    private string original = string.Empty;
    private bool shifted;
    private bool digitsMode;

    public TouchKeyboardOverlay()
    {
        InitializeComponent();
        TouchKeyboardHost.Register(this);
    }

    internal void Open(TextBox textBox)
    {
        target = textBox;
        original = textBox.Text ?? string.Empty;
        shifted = false;
        digitsMode = false;
        ModeButton.Content = "123";
        ShiftButton.Content = "Maj";
        ShiftButton.IsEnabled = true;

        var title = Placeholder.GetText(textBox);
        if (string.IsNullOrWhiteSpace(title))
            title = AutomationProperties.GetName(textBox);
        if (string.IsNullOrWhiteSpace(title) && textBox.ToolTip is string tooltip)
            title = tooltip;
        if (string.IsNullOrWhiteSpace(title))
            title = "Saisie de texte";

        KeyboardTitle.Text = title;
        UpdateDisplay();
        Render();
        Visibility = Visibility.Visible;
        // On ne prend PAS le focus ici : le TextBox cible doit le garder, sinon un binding
        // en UpdateSourceTrigger=LostFocus n'aurait plus jamais d'occasion de se propager.
        textBox.BringIntoView();
    }

    private void UpdateDisplay()
    {
        if (target is null)
        {
            PreviewText.Text = string.Empty;
            PreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        var text = target.Text ?? string.Empty;
        PreviewText.Text = text;
        PreviewPlaceholder.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Render()
    {
        Fill(Row1, DigitOrLetter(0));
        Fill(Row2, DigitOrLetter(1));
        Fill(Row3, DigitOrLetter(2));
        Fill(Row4, DigitOrLetter(3));
    }

    private string[] DigitOrLetter(int row) =>
        digitsMode ? DigitRows[row] : shifted ? LetterRows[row].Select(x => x.ToUpperInvariant()).ToArray() : LetterRows[row];

    private void Fill(UniformGrid panel, string[] keys)
    {
        panel.Children.Clear();
        foreach (var key in keys)
        {
            var button = new Button
            {
                Content = key,
                Tag = key,
                Style = (Style)FindResource("KeyboardButton")
            };
            AutomationProperties.SetName(button, $"Touche {key}");
            button.Click += OnChar;
            panel.Children.Add(button);
        }
    }

    private void Insert(string text)
    {
        if (target is null) return;
        var start = target.SelectionStart;
        var len = target.SelectionLength;
        var current = target.Text ?? string.Empty;
        if (start > current.Length) start = current.Length;

        target.Text = current.Remove(start, len).Insert(start, text);
        target.SelectionStart = start + text.Length;
        target.SelectionLength = 0;
        UpdateDisplay();
    }

    private void OnChar(object sender, RoutedEventArgs e) => Insert((string)((Button)sender).Tag);

    private void OnSpace(object sender, RoutedEventArgs e) => Insert(" ");

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (target is null) return;
        var start = target.SelectionStart;
        var len = target.SelectionLength;
        var current = target.Text ?? string.Empty;

        if (len > 0)
        {
            target.Text = current.Remove(start, len);
            target.SelectionStart = start;
        }
        else if (start > 0 && start <= current.Length)
        {
            target.Text = current.Remove(start - 1, 1);
            target.SelectionStart = start - 1;
        }
        target.SelectionLength = 0;
        UpdateDisplay();
    }

    private void OnClearText(object sender, RoutedEventArgs e)
    {
        if (target is null) return;
        target.Text = string.Empty;
        target.SelectionStart = 0;
        target.SelectionLength = 0;
        UpdateDisplay();
    }

    private void OnShift(object sender, RoutedEventArgs e)
    {
        shifted = !shifted;
        ShiftButton.Content = shifted ? "min" : "Maj";
        Render();
    }

    private void OnMode(object sender, RoutedEventArgs e)
    {
        digitsMode = !digitsMode;
        ModeButton.Content = digitsMode ? "ABC" : "123";
        ShiftButton.IsEnabled = !digitsMode;
        Render();
    }

    // Même règle que le pavé numérique : valider et fermer par le voile conservent la saisie,
    // seul « Annuler » restaure le texte d'origine.
    private void OnOk(object sender, RoutedEventArgs e) => Close();

    private void OnCancel(object sender, MouseButtonEventArgs e) => Close();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Revert();

    private void Revert()
    {
        // Le clavier écrit en direct dans le TextBox : annuler consiste à réécrire l'état initial.
        if (target is not null) { target.Text = original; target.SelectionStart = original.Length; }
        Close();
    }

    private void Close()
    {
        var box = target;
        target = null;
        Visibility = Visibility.Collapsed;
        box?.Focus();
    }

    private void OnCardTap(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
