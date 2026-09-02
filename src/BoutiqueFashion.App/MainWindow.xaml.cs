using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using BoutiqueFashion.App.Controls;
using BoutiqueFashion.App.ViewModels;

namespace BoutiqueFashion.App;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnWindowPreviewMouseDown), true);
        AddHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(OnWindowPreviewTouchDown), true);
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        TryOpenTouchKeyboard(e.OriginalSource as DependencyObject);
    }

    private void OnWindowPreviewTouchDown(object? sender, TouchEventArgs e)
    {
        TryOpenTouchKeyboard(e.OriginalSource as DependencyObject);
    }

    private static void TryOpenTouchKeyboard(DependencyObject? source)
    {
        if (source is null) return;

        var textBox = FindAncestor<TextBox>(source);
        if (textBox is null) return;
        if (textBox.IsReadOnly || !textBox.IsEnabled) return;
        if (TouchKeyboard.GetSuppress(textBox)) return;
        if (IsInsideOverlay(textBox)) return;

        TouchKeyboardHost.Open(textBox);
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        DependencyObject? node = current;
        while (node != null)
        {
            if (node is T match) return match;
            if (node is Visual || node is Visual3D)
                node = VisualTreeHelper.GetParent(node);
            else
                node = LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private static bool IsInsideOverlay(DependencyObject element)
    {
        DependencyObject? node = element;
        while (node != null)
        {
            if (node is TouchKeyboardOverlay || node is TouchKeypadOverlay) return true;
            if (node is Visual || node is Visual3D)
                node = VisualTreeHelper.GetParent(node);
            else
                node = LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
}
