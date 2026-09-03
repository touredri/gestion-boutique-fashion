using System.Windows;
using System.Windows.Automation;
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
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(KeypadField), new PropertyMetadata(string.Empty, OnTitleChanged));

    /// <summary>À passer à False quand un TextBlock FieldLabel voisin porte déjà le libellé.</summary>
    public static readonly DependencyProperty ShowTitleProperty =
        DependencyProperty.Register(nameof(ShowTitle), typeof(bool), typeof(KeypadField), new PropertyMetadata(true, OnAppearanceChanged));

    public KeypadField()
    {
        InitializeComponent();
        // Les callbacks de DP se déclenchent avant InitializeComponent : sans ce rappel,
        // un Title posé littéralement en XAML resterait invisible.
        RefreshDisplay();
    }

    public string? Value { get => (string?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool Mask { get => (bool)GetValue(MaskProperty); set => SetValue(MaskProperty, value); }
    public string? Suffix { get => (string?)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
    public string? Placeholder { get => (string?)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public bool ShowTitle { get => (bool)GetValue(ShowTitleProperty); set => SetValue(ShowTitleProperty, value); }

    private static void OnAppearanceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((KeypadField)d).RefreshDisplay();

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var field = (KeypadField)d;
        // Un Title renseigné vaut nom accessible tant qu'aucun n'a été posé explicitement.
        if (field.Title is { Length: > 0 } title && string.IsNullOrEmpty(AutomationProperties.GetName(field)))
            AutomationProperties.SetName(field, title);
        field.RefreshDisplay();
    }

    // Seul Title pilote le libellé visible : se replier sur AutomationProperties.Name
    // doublonnerait avec les TextBlock FieldLabel frères de Catalog et Sale.
    private string EffectiveLabel => string.IsNullOrWhiteSpace(Title) ? string.Empty : Title!;

    private void RefreshDisplay()
    {
        if (DisplayText is null || LabelText is null) return;
        var muted = (Brush)FindResource("Muted");
        var ink = (Brush)FindResource("Ink");

        var label = ShowTitle ? EffectiveLabel : string.Empty;
        LabelText.Text = label;
        LabelText.Visibility = label.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (Mask)
        {
            var filled = string.IsNullOrEmpty(Value) ? 0 : Math.Min(Value.Length, 12);
            if (filled == 0)
            {
                // Deux champs PIN voisins ne doivent pas se ressembler quand ils sont vides.
                DisplayText.Text = string.IsNullOrEmpty(Placeholder) ? "••••" : Placeholder;
                DisplayText.Foreground = muted;
                return;
            }
            DisplayText.Text = new string('●', filled);
            DisplayText.Foreground = ink;
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
        Focus();
        KeypadHost.Open(this);
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space or Key.F4)
        {
            KeypadHost.Open(this);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    // Couleur de bordure seule : épaissir décalerait le contenu de 1 px (UseLayoutRounding est actif).
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (Surface is not null) Surface.BorderBrush = (Brush)FindResource("Terracotta");
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        if (Surface is not null) Surface.BorderBrush = (Brush)FindResource("LineStrong");
    }
}
