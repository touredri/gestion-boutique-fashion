using BoutiqueFashion.Infrastructure;
using BoutiqueFashion.Testing;

namespace BoutiqueFashion.Tests;

/// <summary>
/// Le garde-fou du lot 5 côté terminal. Il échoue au moment où la migration est écrite, et non
/// six mois plus tard sur une caisse à Marcory qui vient de retomber en version précédente.
/// </summary>
public class MigrationCompatibilityTests
{
    [Fact]
    public void Migrations_stay_readable_by_the_previous_version()
    {
        var violations = MigrationCompatibility.Inspect(
            typeof(BoutiqueDbContext).Assembly, "Microsoft.EntityFrameworkCore.Sqlite");

        Assert.True(violations.Count == 0,
            "Migrations non rétrocompatibles — un retour arrière du binaire laisserait le schéma en avance :"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations.Select(v => "  · " + v))
            + Environment.NewLine
            + "Voir docs/lot5-mises-a-jour-a-distance.md, section « Le point dur ».");
    }

    /// <summary>
    /// Vérifie que le contrôle attrape bien quelque chose. Sans ce test, une erreur de réflexion
    /// donnerait zéro migration inspectée, zéro infraction, et un test vert qui ne garde rien.
    /// </summary>
    [Fact]
    public void The_checker_actually_sees_the_migrations()
    {
        var migrations = typeof(BoutiqueDbContext).Assembly.GetTypes()
            .Count(t => !t.IsAbstract && typeof(Microsoft.EntityFrameworkCore.Migrations.Migration).IsAssignableFrom(t));

        Assert.True(migrations >= 5, $"Seulement {migrations} migrations vues par réflexion.");
    }
}
