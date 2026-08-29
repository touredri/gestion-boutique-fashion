using System.Windows;
using System.Windows.Controls;
using BoutiqueFashion.App.Controls;
using BoutiqueFashion.App.ViewModels;

namespace BoutiqueFashion.App;

public partial class MainWindow : Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        AddHandler(UIElement.GotFocusEvent, new RoutedEventHandler(OnControlFocus), true);
    }

    private static void OnControlFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TextBox textBox && !TouchKeyboard.GetSuppress(textBox))
            TouchKeyboardHost.Open(textBox);
    }
}
