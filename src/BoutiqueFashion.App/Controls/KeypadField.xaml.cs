using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace BoutiqueFashion.App.Controls;

public partial class KeypadField : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(KeypadField),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnAppearanceChanged));

    public static readonly DependencyProperty MaskProperty =
        DependencyProperty.Register(nameof(Mask), typeof(bool), typeof(KeypadField), new PropertyMetadata(false, OnAppearanceChanged));

    public static readonly DependencyProperty SuffixProperty =
        DependencyProperty.Register(nameof(Suffix), typeof(string), typeof(KeypadField), new PropertyMetadata(string.Empty, OnAppearanceChanged));

    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.Register(nameof(Placeholder), typeof(string), typeof(KeypadField), new PropertyMetadata(string.Empty, OnAppearanceChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(KeypadField), new PropertyMetadata(string.Empty));

    public KeypadField() => InitializeComponent();

    public string? Value { get => (string?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool Mask { get => (bool)GetValue(MaskProperty); set => SetValue(MaskProperty, value); }
    public string? Suffix { get => (string?)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
    public string? Placeholder { get => (string?)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    private static void OnAppearanceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((KeypadField)d).RefreshDisplay();

    private void RefreshDisplay()
    {
        if (DisplayText is null) return;
        var muted = (Brush)FindResource("Muted");
        var ink = (Brush)FindResource("Ink");
        if (Mask)
        {
            var filled = string.IsNullOrEmpty(Value) ? 0 : Math.Min(Value.Length, 12);
            DisplayText.Text = filled == 0 ? "••••" : new string('●', filled);
            DisplayText.Foreground = filled == 0 ? muted : ink;
            return;
        }
        if (string.IsNullOrEmpty(Value))
        {
            DisplayText.Text = string.IsNullOrEmpty(Placeholder) ? "Toucher pour saisir" : Placeholder;
            DisplayText.Foreground = muted;
            return;
        }
        DisplayText.Text = string.IsNullOrEmpty(Suffix) ? Value : $"{Value} {Suffix}";
        DisplayText.Foreground = ink;
    }

    private void OnTap(object sender, MouseButtonEventArgs e)
    {
        KeypadHost.Open(this);
        e.Handled = true;
    }
}
