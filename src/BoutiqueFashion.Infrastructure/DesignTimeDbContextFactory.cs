using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BoutiqueFashion.Infrastructure;

/// <summary>
/// Utilisée exclusivement par « dotnet ef » au moment de générer une migration.
/// Sans elle, l'outil ne saurait pas construire un <see cref="BoutiqueDbContext"/> : son
/// constructeur exige des options, et l'hôte qui les fournit d'ordinaire est l'application WPF,
/// que l'outillage ne démarre pas.
///
/// Le chemin de base est fictif et n'est jamais ouvert : seule la forme du modèle est lue.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BoutiqueDbContext>
{
    public BoutiqueDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<BoutiqueDbContext>()
            .UseSqlite("Data Source=design-time-only.db")
            .Options);
}
