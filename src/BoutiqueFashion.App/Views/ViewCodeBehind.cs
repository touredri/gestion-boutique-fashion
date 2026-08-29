using System.Windows.Controls;
using BoutiqueFashion.App.ViewModels;

namespace BoutiqueFashion.App.Views;

public partial class DashboardView : UserControl { public DashboardView() => InitializeComponent(); }
public partial class SaleView : UserControl { public SaleView() => InitializeComponent(); }
public partial class CatalogView : UserControl
{
    public CatalogView() => InitializeComponent();
    private void BrowsePhoto_OnClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not CatalogViewModel vm) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choisir une photo de produit",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
            vm.PhotoPath = dialog.FileName;
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
