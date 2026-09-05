using BoutiqueFashion.Server.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;

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
    private const string DefaultAdmin = "test-admin-key";
    private readonly string database = $"boutique_test_{Guid.NewGuid():N}";

    private static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("BOUTIQUE_TEST_POSTGRES")
        ?? "Host=localhost;Port=5432;Username=boutique;Password=boutique";

    private string ConnectionString => $"{BaseConnectionString};Database={database}";

    public HttpClient Admin { get; private set; } = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", ConnectionString);
        builder.UseSetting("Admin:ApiKey", DefaultAdmin);
    }

    public async Task InitializeAsync()
    {
        await using (var scope = Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
        }

        Admin = CreateClient();
        Admin.DefaultRequestHeaders.Add("X-Admin-Key", DefaultAdmin);
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
