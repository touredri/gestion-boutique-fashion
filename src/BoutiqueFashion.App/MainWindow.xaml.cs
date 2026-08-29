using System.Windows;
using BoutiqueFashion.App.ViewModels;
namespace BoutiqueFashion.App;
public partial class MainWindow : Window { public MainWindow(ShellViewModel viewModel) { InitializeComponent(); DataContext = viewModel; } }
