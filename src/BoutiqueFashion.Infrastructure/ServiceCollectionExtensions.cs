using BoutiqueFashion.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BoutiqueFashion.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddBoutiqueInfrastructure(this IServiceCollection services, string? dataRoot = null)
    {
        var paths = new AppPaths(dataRoot); services.AddSingleton(paths);
        services.AddDbContextFactory<BoutiqueDbContext>(options => options.UseSqlite($"Data Source={paths.Database};Cache=Shared"));
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IAuthorizationService, AuthorizationService>();
        services.AddSingleton<IAppSettingsService, AppSettingsService>();
        services.AddSingleton<ICatalogService, CatalogService>();
        services.AddSingleton<IStockService, StockService>();
        services.AddSingleton<ICustomerService, CustomerService>();
        services.AddSingleton<IExpenseService, ExpenseService>();
        services.AddSingleton<ICreditService, CreditService>();
        services.AddSingleton<IReturnService, ReturnService>();
        services.AddSingleton<IInventoryService, InventoryService>();
        services.AddSingleton<IDocumentService, DocumentService>();
        services.AddSingleton<ISaleService, SaleService>();
        services.AddSingleton<ICashSessionService, CashSessionService>();
        services.AddSingleton<IReportService, ReportService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IProductImportService, ProductImportService>();
        services.AddSingleton<IPrintQueueService, PrintQueueService>();
        services.AddSingleton<IThermalPrinterService, ThermalPrinterService>();
        services.AddSingleton<IA4DocumentService, A4DocumentService>();
        return services;
    }
}
