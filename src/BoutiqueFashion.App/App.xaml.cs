using System.Windows;
using BoutiqueFashion.App.ViewModels;
using BoutiqueFashion.Application;
using BoutiqueFashion.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BoutiqueFashion.App;

public partial class App : System.Windows.Application
{
    private readonly System.Windows.Threading.DispatcherTimer backupTimer = new() { Interval = TimeSpan.FromHours(24) };
    private readonly IHost host = Host.CreateDefaultBuilder().ConfigureServices(services =>
    {
        services.AddBoutiqueInfrastructure();
        services.AddSingleton<ShellViewModel>(); services.AddSingleton<DashboardViewModel>(); services.AddSingleton<SaleViewModel>();
        services.AddSingleton<CatalogViewModel>(); services.AddSingleton<StockViewModel>(); services.AddSingleton<CustomersViewModel>();
        services.AddSingleton<ExpensesViewModel>(); services.AddSingleton<DocumentsViewModel>(); services.AddSingleton<ReportsViewModel>(); services.AddSingleton<SettingsViewModel>(); services.AddSingleton<MainWindow>();
    }).Build();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); await host.StartAsync();
        await host.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        await TryBackupAsync(); backupTimer.Tick += async (_, _) => await TryBackupAsync(); backupTimer.Start();
        OpenTouchKeyboard();
        var window = host.Services.GetRequiredService<MainWindow>(); window.Show();
        await host.Services.GetRequiredService<ShellViewModel>().InitializeAsync();
    }

    private static void OpenTouchKeyboard()
    {
        try
        {
            var tabTip = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesCommon), "microsoft shared", "ink", "TabTip.exe");
            if (System.IO.File.Exists(tabTip)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tabTip) { UseShellExecute = true });
        }
        catch { /* terminal avec clavier physique : rien à faire */ }
    }

    private async Task TryBackupAsync() { try { await host.Services.GetRequiredService<IBackupService>().CreateAsync(); } catch { /* L'échec reste non bloquant; la sauvegarde manuelle affiche l'erreur. */ } }
    protected override async void OnExit(ExitEventArgs e) { backupTimer.Stop(); await host.StopAsync(); host.Dispose(); base.OnExit(e); }
}
