using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BoutiqueFashion.App.ViewModels;
using BoutiqueFashion.Application;

namespace BoutiqueFashion.App.Views;

public partial class DashboardView : UserControl { public DashboardView() => InitializeComponent(); }
public partial class CashView : UserControl { public CashView() => InitializeComponent(); }
public partial class AdvancesView : UserControl { public AdvancesView() => InitializeComponent(); }
public partial class OrdersView : UserControl { public OrdersView() => InitializeComponent(); }
public partial class SaleView : UserControl
{
    public SaleView() => InitializeComponent();
    private void SearchBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (DataContext is SaleViewModel vm) vm.SearchProductsCommand.Execute(null);
    }
}
public partial class CatalogView : UserControl
{
    public CatalogView() => InitializeComponent();
    private void BrowseRowPhoto_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if ((sender as System.Windows.FrameworkElement)?.DataContext is not VariantRowViewModel row) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choisir une photo de variante",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
            row.PhotoPath = dialog.FileName;
    }
}
public partial class CustomersView : UserControl { public CustomersView() => InitializeComponent(); }
public partial class ExpensesView : UserControl { public ExpensesView() => InitializeComponent(); }
public partial class DocumentsView : UserControl { public DocumentsView() => InitializeComponent(); }
public partial class ReportsView : UserControl { public ReportsView() => InitializeComponent(); }
public partial class StockView : UserControl { public StockView() => InitializeComponent(); }
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void BrowseImage(string title, System.Action<string> apply)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true) apply(dialog.FileName);
    }

    private void BrowseLogo_OnClick(object sender, System.Windows.RoutedEventArgs e) => BrowseImage("Choisir le logo", path => { if (DataContext is SettingsViewModel vm) vm.LogoPath = path; });
    private void BrowseStamp_OnClick(object sender, System.Windows.RoutedEventArgs e) => BrowseImage("Choisir le cachet", path => { if (DataContext is SettingsViewModel vm) vm.StampPath = path; });
    private void BrowseSignature_OnClick(object sender, System.Windows.RoutedEventArgs e) => BrowseImage("Choisir la signature", path => { if (DataContext is SettingsViewModel vm) vm.SignaturePath = path; });
}

public partial class TicketPreviewWindow : Window
{
    private TicketPreviewWindow() => InitializeComponent();

    public static void Show(IReadOnlyList<TicketLine> lines, string title, BoutiqueFashion.Domain.PaperWidth paper = BoutiqueFashion.Domain.PaperWidth.Mm80)
    {
        var window = new TicketPreviewWindow { Title = title };
        window.PaperHeader.Text = $"APERÇU TICKET · {(int)paper} MM";
        window.HeaderText.Text = title.ToUpperInvariant();
        window.TicketLines.ItemsSource = lines.Select(line => new TicketPreviewLine(line)).ToArray();
        window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();
    }

    private void Close_OnClick(object sender, RoutedEventArgs e) => Close();
}

public sealed class TicketPreviewLine(TicketLine line)
{
    public string Text { get; } = string.IsNullOrEmpty(line.Text) ? " " : line.Text;
    public FontWeight Weight => line.Bold ? FontWeights.Bold : FontWeights.Normal;
    public double Size => line.DoubleHeight ? 24 : 13;
}
