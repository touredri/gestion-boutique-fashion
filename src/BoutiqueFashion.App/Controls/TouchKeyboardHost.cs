using System.Windows;

namespace BoutiqueFashion.App.Controls;

public static class Placeholder
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Placeholder), new PropertyMetadata(string.Empty));

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);
    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);
}

public static class TouchKeyboard
{
    public static readonly DependencyProperty SuppressProperty =
        DependencyProperty.RegisterAttached("Suppress", typeof(bool), typeof(TouchKeyboard), new PropertyMetadata(false));

    public static bool GetSuppress(DependencyObject element) => (bool)element.GetValue(SuppressProperty);
    public static void SetSuppress(DependencyObject element, bool value) => element.SetValue(SuppressProperty, value);
}

internal static class TouchKeyboardHost
{
    private static TouchKeyboardOverlay? overlay;

    internal static void Register(TouchKeyboardOverlay instance) => overlay = instance;

    public static void Open(System.Windows.Controls.TextBox target) => overlay?.Open(target);
}
