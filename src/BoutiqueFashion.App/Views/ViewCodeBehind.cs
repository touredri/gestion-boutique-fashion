using System.Windows.Controls;
using BoutiqueFashion.App.ViewModels;

namespace BoutiqueFashion.App.Views;

public partial class DashboardView : UserControl { public DashboardView() => InitializeComponent(); }
public partial class SaleView : UserControl { public SaleView() => InitializeComponent(); private void PinBox_OnPasswordChanged(object sender, System.Windows.RoutedEventArgs e) { if (DataContext is SaleViewModel vm) vm.ManagerPin = ((PasswordBox)sender).Password; } }
public partial class CatalogView : UserControl
{
    public CatalogView() => InitializeComponent();
    private void CatalogPin_OnPasswordChanged(object sender, System.Windows.RoutedEventArgs e) { if (DataContext is CatalogViewModel vm) vm.ManagerPin = ((PasswordBox)sender).Password; }
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
public partial class CustomersView : UserControl { public CustomersView() => InitializeComponent();private void CreditPin_OnPasswordChanged(object sender,System.Windows.RoutedEventArgs e){if(DataContext is CustomersViewModel vm)vm.ManagerPin=((PasswordBox)sender).Password;} }
public partial class ExpensesView : UserControl { public ExpensesView() => InitializeComponent(); }
public partial class DocumentsView:UserControl{public DocumentsView()=>InitializeComponent();private void DocumentPin_OnPasswordChanged(object sender,System.Windows.RoutedEventArgs e){if(DataContext is DocumentsViewModel vm)vm.ManagerPin=((PasswordBox)sender).Password;}}
public partial class ReportsView : UserControl { public ReportsView() => InitializeComponent(); }
public partial class StockView : UserControl
{
    public StockView() => InitializeComponent();
    private void PinBox_OnPasswordChanged(object sender, System.Windows.RoutedEventArgs e) { if (DataContext is StockViewModel vm) vm.ManagerPin = ((PasswordBox)sender).Password; }
}
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
    private void PinBox_OnPasswordChanged(object sender, System.Windows.RoutedEventArgs e) { if (DataContext is SettingsViewModel vm) vm.Pin = ((PasswordBox)sender).Password; }
}
