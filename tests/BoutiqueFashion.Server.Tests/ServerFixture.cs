using System.Net.Http.Json;
using System.Text.Json;
using BoutiqueFashion.Server.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace BoutiqueFashion.Server.Tests;

/// <summary>
/// Serveur réel sur un PostgreSQL réel. Pas de base en mémoire : les garanties qu'on veut
/// vérifier — index uniques par boutique, séquence de curseur, transactions — n'existent que
/// dans un vrai moteur, et une base en mémoire les simulerait toutes avec complaisance.
///
/// Chaque classe de test reçoit sa propre base, créée puis détruite : les tests peuvent alors
/// s'exécuter en parallèle sans se marcher dessus.
/// </summary>
public sealed class ServerFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string OwnerUsername = "proprietaire";
    public const string OwnerPassword = "motdepasse-de-test";
    private readonly string database = $"boutique_test_{Guid.NewGuid():N}";

    // Pas de repli codé en dur : un identifiant de secours dans un fichier versionné finit
    // par ressembler à un secret, et surtout il masque la vraie cause quand la variable
    // manque — on obtient une erreur de connexion Npgsql au lieu de la consigne à suivre.
    private static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("BOUTIQUE_TEST_POSTGRES")
        ?? throw new InvalidOperationException(
            "BOUTIQUE_TEST_POSTGRES n'est pas définie. Démarrez la base de développement " +
            "(docker compose -f docker/compose.dev.yml up postgres) puis exportez " +
            "BOUTIQUE_TEST_POSTGRES=\"Host=localhost;Port=5432;Username=boutique\".");

    private string ConnectionString => $"{BaseConnectionString};Database={database}";

    public HttpClient Admin { get; set; } = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", ConnectionString);
        builder.UseSetting("Bootstrap:Username", OwnerUsername);
        builder.UseSetting("Bootstrap:Password", OwnerPassword);
    }

    public async Task InitializeAsync()
    {
        await using (var scope = Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            // L'environnement Testing court-circuite la migration au démarrage, donc aussi
            // l'amorçage du premier compte : on le fait ici avec la même méthode que la production.
            await Sync.UserAuthentication.EnsureFirstUserAsync(db, scope.ServiceProvider.GetRequiredService<IConfiguration>());
        }

        Admin = await SignInAsync(OwnerUsername, OwnerPassword)
            ?? throw new InvalidOperationException("Le compte de test n'a pas pu se connecter.");
    }

    /// <summary>Client authentifié, ou <c>null</c> si les identifiants sont refusés.</summary>
    public async Task<HttpClient?> SignInAsync(string username, string password)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { Username = username, Password = password });
        if (!response.IsSuccessStatusCode) { client.Dispose(); return null; }

        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    public new async Task DisposeAsync()
    {
        await using (var scope = Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
            await db.Database.EnsureDeletedAsync();
        }
        Admin.Dispose();
        await base.DisposeAsync();
    }

    /// <summary>Client anonyme : ni clé d'administration, ni jeton d'appareil.</summary>
    public HttpClient Anonymous() => CreateClient();

    public HttpClient AsDevice(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    public async Task<T> InDbAsync<T>(Func<ServerDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ServerDbContext>());
    }
}
