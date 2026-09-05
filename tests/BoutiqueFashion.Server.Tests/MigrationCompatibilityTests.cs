using BoutiqueFashion.Server.Data;
using BoutiqueFashion.Testing;

namespace BoutiqueFashion.Server.Tests;

/// <summary>
/// Même garde-fou que côté terminal, sur le contexte serveur. Le serveur se met à jour par
/// « docker compose pull » et non par Velopack, mais un conteneur qu'on redémarre sur l'image
/// précédente pose exactement le même problème de schéma en avance.
/// </summary>
public class MigrationCompatibilityTests
{
    [Fact]
    public void Migrations_stay_readable_by_the_previous_version()
    {
        var violations = MigrationCompatibility.Inspect(
            typeof(ServerDbContext).Assembly, "Npgsql.EntityFrameworkCore.PostgreSQL");

        Assert.True(violations.Count == 0,
            "Migrations serveur non rétrocompatibles :"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations.Select(v => "  · " + v)));
    }

    [Fact]
    public void The_checker_actually_sees_the_migrations()
    {
        var migrations = typeof(ServerDbContext).Assembly.GetTypes()
            .Count(t => !t.IsAbstract && typeof(Microsoft.EntityFrameworkCore.Migrations.Migration).IsAssignableFrom(t));

        Assert.True(migrations >= 3, $"Seulement {migrations} migrations vues par réflexion.");
    }
}
