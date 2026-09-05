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
            // Ni identifiant ni mot de passe : aucune connexion n'est ouverte ici, seul le
            // fournisseur compte pour produire du SQL PostgreSQL. Écrire des identifiants
            // factices reviendrait à laisser traîner quelque chose qui ressemble à un secret.
            .UseNpgsql("Host=localhost;Database=design-time-only")
            .Options);
}
