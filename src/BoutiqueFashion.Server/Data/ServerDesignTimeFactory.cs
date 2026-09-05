using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BoutiqueFashion.Server.Data;

/// <summary>
/// Utilisée uniquement par « dotnet ef ». Elle évite de démarrer l'application pour générer une
/// migration — ce qui exigerait une base joignable, alors que seule la forme du modèle est lue.
/// </summary>
public sealed class ServerDesignTimeFactory : IDesignTimeDbContextFactory<ServerDbContext>
{
    public ServerDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ServerDbContext>()
            .UseNpgsql("Host=localhost;Database=design-time-only;Username=postgres;Password=postgres")
            .Options);
}
