using BoutiqueFashion.Domain;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Infrastructure;

public sealed class DatabaseInitializer(IDbContextFactory<BoutiqueDbContext> factory)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken);
        if (!await db.AppSettings.AnyAsync(x => x.Key == "Shop.Name", cancellationToken))
        {
            db.AppSettings.AddRange(
                new AppSetting { Key = "Shop.Name", Value = "Ma Boutique" },
                new AppSetting { Key = "Shop.Currency", Value = "FCFA" },
                new AppSetting { Key = "Shop.Footer", Value = "Merci de votre visite" });
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
