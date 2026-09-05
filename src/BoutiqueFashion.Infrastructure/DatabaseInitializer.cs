using BoutiqueFashion.Domain;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Infrastructure;

public sealed class DatabaseInitializer(IDbContextFactory<BoutiqueDbContext> factory)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await BaselineLegacyDatabaseAsync(db, cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
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

    /// <summary>
    /// Les bases antérieures aux migrations ont été créées par <c>EnsureCreated</c> : elles portent
    /// déjà le schéma d'<c>InitialCreate</c>, mais aucune trace dans <c>__EFMigrationsHistory</c>.
    /// Sans ce marquage, <c>MigrateAsync</c> rejouerait <c>InitialCreate</c> et échouerait sur des
    /// tables déjà présentes — l'application ne démarrerait plus, données intactes mais inaccessibles.
    ///
    /// On déclare donc la première migration comme déjà appliquée, et les suivantes s'enchaînent
    /// normalement. Sur une base neuve, cette méthode ne fait rien.
    /// </summary>
    private static async Task BaselineLegacyDatabaseAsync(BoutiqueDbContext db, CancellationToken cancellationToken)
    {
        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'table'")
            .ToListAsync(cancellationToken);

        // Base neuve : MigrateAsync va tout créer, il n'y a rien à reprendre.
        if (!tables.Contains("AppSettings")) return;
        // Base déjà passée sous migrations : ne pas y toucher.
        if (tables.Contains("__EFMigrationsHistory")) return;

        // Le premier identifiant de migration est lu dans l'assembly plutôt que codé en dur :
        // un InitialCreate régénéré porterait un autre horodatage.
        var initial = db.Database.GetMigrations().FirstOrDefault();
        if (initial is null) return;

        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            );
            """, cancellationToken);

        // Surcharge IEnumerable<object> explicite : la forme « params object[] » créerait une
        // ambiguïté avec le CancellationToken en dernier argument.
        var parameters = new object[] { initial, typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "10.0.0" };
        await db.Database.ExecuteSqlRawAsync(
            """INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ({0}, {1})""",
            parameters,
            cancellationToken);
    }
}
