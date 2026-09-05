using System.Windows;
using BoutiqueFashion.App.ViewModels;
using BoutiqueFashion.Application;
using BoutiqueFashion.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BoutiqueFashion.App;

public partial class App : System.Windows.Application
{
    /// <summary>
    /// Point d'entrée repris à WPF pour une seule raison : <c>VelopackApp.Build().Run()</c> doit
    /// s'exécuter avant que quoi que ce soit d'autre ne démarre. C'est lui qui reprend la main
    /// sur les lancements particuliers qui suivent une installation — sans lui, l'application
    /// s'ouvrirait normalement au lieu de terminer sa mise à jour.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        Velopack.VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    private readonly System.Windows.Threading.DispatcherTimer backupTimer = new() { Interval = TimeSpan.FromHours(24) };
    private readonly IHost host = Host.CreateDefaultBuilder().ConfigureServices(services =>
    {
        services.AddBoutiqueInfrastructure();
        services.AddSingleton<ManagerSession>(); services.AddSingleton<ShiftSession>(); services.AddSingleton<SyncAgent>(); services.AddSingleton<UpdateAgent>();
        services.AddSingleton<ShellViewModel>(); services.AddSingleton<DashboardViewModel>(); services.AddSingleton<SaleViewModel>(); services.AddSingleton<CashViewModel>(); services.AddSingleton<AdvancesViewModel>(); services.AddSingleton<OrdersViewModel>();
        services.AddSingleton<CatalogViewModel>(); services.AddSingleton<StockViewModel>(); services.AddSingleton<CustomersViewModel>();
        services.AddSingleton<ExpensesViewModel>(); services.AddSingleton<DocumentsViewModel>(); services.AddSingleton<ReportsViewModel>(); services.AddSingleton<SettingsViewModel>(); services.AddSingleton<MainWindow>();
    }).Build();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); await host.StartAsync();
        await host.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        await TryBackupAsync(); backupTimer.Tick += async (_, _) => await TryBackupAsync(); backupTimer.Start();
        var window = host.Services.GetRequiredService<MainWindow>(); window.Show();
        await host.Services.GetRequiredService<ShellViewModel>().InitializeAsync();
        // Démarré après la fenêtre : la caisse doit être utilisable même si le
        // serveur est injoignable, donc la synchronisation ne retarde rien.
        await host.Services.GetRequiredService<SyncAgent>().StartAsync();
        // Après la synchronisation : la recherche de mise à jour a besoin de l'adresse du serveur
        // et du jeton, qui n'existent qu'une fois le terminal appairé.
        await host.Services.GetRequiredService<UpdateAgent>().StartAsync();
    }

    private async Task TryBackupAsync() { try { await host.Services.GetRequiredService<IBackupService>().CreateAsync(); } catch { /* L'échec reste non bloquant; la sauvegarde manuelle affiche l'erreur. */ } }
    protected override async void OnExit(ExitEventArgs e)
    {
        backupTimer.Stop();
        // La mise à jour s'installe ici, à la fermeture, et jamais pendant. La boutique ferme le
        // soir, l'échange de fichiers se fait derrière, elle rouvre le lendemain sur la nouvelle
        // version. UpdateAgent refuse si une vacation est ouverte — voir §4 du plan du lot 5.
        try { await host.Services.GetRequiredService<UpdateAgent>().ApplyOnExitAsync(); }
        catch { /* Une mise à jour ratée ne doit pas empêcher l'application de se fermer. */ }
        await host.StopAsync(); host.Dispose(); base.OnExit(e);
    }
}
