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
        ["w", "x", "c", "v", "b", "n", ",", ";", ":", "!"]
    ];

    private static readonly string[][] DigitRows =
    [
        ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"],
        ["@", "#", "€", "$", "%", "*", "+", "-", "=", "/"],
        ["(", ")", "_", ".", "?", "'", "\"", "&", "<", ">"]
    ];

    private TextBox? target;
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
        shifted = false;
        digitsMode = false;
        ModeButton.Content = "123";
        Render();
        Visibility = Visibility.Visible;
    }

    private void Render()
    {
        Fill(Row1, DigitOrLetter(0));
        Fill(Row2, DigitOrLetter(1));
        Fill(Row3, DigitOrLetter(2));
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
        target.Text = target.Text.Remove(start, target.SelectionLength).Insert(start, text);
        target.SelectionStart = start + text.Length;
    }

    private void OnChar(object sender, RoutedEventArgs e) => Insert((string)((Button)sender).Tag);

    private void OnSpace(object sender, RoutedEventArgs e) => Insert(" ");

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (target is null) return;
        var start = target.SelectionStart;
        if (target.SelectionLength > 0) { target.Text = target.Text.Remove(start, target.SelectionLength); target.SelectionStart = start; return; }
        if (start > 0) { target.Text = target.Text.Remove(start - 1, 1); target.SelectionStart = start - 1; }
    }

    private void OnShift(object sender, RoutedEventArgs e) { shifted = !shifted; Render(); }

    private void OnMode(object sender, RoutedEventArgs e)
    {
        digitsMode = !digitsMode;
        ModeButton.Content = digitsMode ? "ABC" : "123";
        Render();
    }

    private void OnOk(object sender, RoutedEventArgs e) => Close();

    private void OnCancel(object sender, MouseButtonEventArgs e) => Close();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void Close()
    {
        target = null;
        Visibility = Visibility.Collapsed;
    }

    private void OnCardTap(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
