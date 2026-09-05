using BoutiqueFashion.Domain;
using BoutiqueFashion.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BoutiqueFashion.Tests;

/// <summary>
/// Reprise des bases antérieures aux migrations. C'est le scénario le plus coûteux à rater :
/// une erreur ici ne se voit qu'au premier démarrage sur un terminal en service, données
/// intactes mais application morte.
/// </summary>
public sealed class DatabaseMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"boutique-migration-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddBoutiqueInfrastructure(root);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Ramène le schéma à celui d'<c>InitialCreate</c>, c'est-à-dire à ce que produisait
    /// <c>EnsureCreated</c> avant l'arrivée des migrations.
    ///
    /// À appeler une fois les données en place : le modèle EF connaît toujours ces colonnes, donc
    /// tout INSERT postérieur à leur suppression échouerait.
    ///
    /// <b>À compléter à chaque migration qui ajoute une table ou une colonne</b> — sinon ce test
    /// échoue en annonçant que la table existe déjà, ce qui est précisément son travail.
    /// </summary>
    private static async Task RewindToInitialSchemaAsync(BoutiqueDbContext db)
    {
        foreach (var statement in new[]
        {
            // CashShiftAndReservations
            """ALTER TABLE "CashSessions" DROP COLUMN "OperatorName";""",
            """ALTER TABLE "CashSessions" DROP COLUMN "OperatorPinHash";""",
            """ALTER TABLE "CashSessions" DROP COLUMN "ClosedBy";""",
            """ALTER TABLE "ProductVariants" DROP COLUMN "QuantityReserved";""",
            // CashMovements
            """DROP TABLE IF EXISTS "CashMovements";""",
            // SyncOutbox
            """DROP TABLE IF EXISTS "SyncOutbox";""",
        })
        {
            await db.Database.ExecuteSqlRawAsync(statement);
        }
    }

    [Fact]
    public async Task Legacy_database_is_baselined_migrated_and_keeps_its_data()
    {
        var categoryId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var variantId = Guid.NewGuid();

        using (var provider = BuildProvider())
        {
            var factory = provider.GetRequiredService<IDbContextFactory<BoutiqueDbContext>>();
            await using var db = await factory.CreateDbContextAsync();

            // EnsureCreated produit le schéma courant : on garnit d'abord la base, puis on la
            // ramène au schéma d'origine. L'ordre inverse ferait insérer des colonnes disparues.
            await db.Database.EnsureCreatedAsync();
            db.AppSettings.Add(new AppSetting { Key = "Shop.Name", Value = "Boutique héritée" });
            db.Categories.Add(new Category { Id = categoryId, Name = "Vêtements" });
            db.Products.Add(new Product { Id = productId, Name = "Robe ancienne", CategoryId = categoryId });
            db.ProductVariants.Add(new ProductVariant { Id = variantId, ProductId = productId, Sku = "OLD-01", CostXof = 5_000, PriceXof = 12_000, QuantityOnHand = 7 });
            await db.SaveChangesAsync();

            await RewindToInitialSchemaAsync(db);
        }
        SqliteConnection.ClearAllPools();

        // Démarrage de l'application sur cette base : c'est ce que fera le terminal.
        using (var provider = BuildProvider())
        {
            await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync();

            var factory = provider.GetRequiredService<IDbContextFactory<BoutiqueDbContext>>();
            await using var db = await factory.CreateDbContextAsync();

            // Les données d'origine survivent — le point qui compte vraiment.
            Assert.Equal("Boutique héritée", await db.AppSettings.Where(x => x.Key == "Shop.Name").Select(x => x.Value).SingleAsync());
            var variant = await db.ProductVariants.SingleAsync(x => x.Id == variantId);
            Assert.Equal(7, variant.QuantityOnHand);
            Assert.Equal("OLD-01", variant.Sku);

            // Et les colonnes du lot 1 sont bien là, à leur valeur par défaut.
            Assert.Equal(0, variant.QuantityReserved);

            // L'historique porte les deux migrations : la baseline puis celle réellement appliquée.
            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            Assert.Equal(db.Database.GetMigrations().Count(), applied.Count);
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
    }

    [Fact]
    public async Task Fresh_database_applies_every_migration()
    {
        using var provider = BuildProvider();
        await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync();

        var factory = provider.GetRequiredService<IDbContextFactory<BoutiqueDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal("Ma Boutique", await db.AppSettings.Where(x => x.Key == "Shop.Name").Select(x => x.Value).SingleAsync());
    }

    [Fact]
    public async Task Initialisation_is_idempotent()
    {
        using var provider = BuildProvider();
        var initializer = provider.GetRequiredService<DatabaseInitializer>();

        // Chaque lancement de l'application rejoue l'initialisation : elle ne doit ni échouer,
        // ni redéclarer une migration, ni réinsérer les paramètres par défaut.
        await initializer.InitializeAsync();
        await initializer.InitializeAsync();

        var factory = provider.GetRequiredService<IDbContextFactory<BoutiqueDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.Single(await db.AppSettings.Where(x => x.Key == "Shop.Name").ToListAsync());
    }
}
