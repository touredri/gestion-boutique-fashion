using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BoutiqueFashion.Server.Data;
using BoutiqueFashion.Server.Sync;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Server.Endpoints;

/// <summary>Un fichier de mise à jour, tel que « vpk pack » l'écrit dans releases.{canal}.json.
/// Les noms sont ceux de Velopack : ce contrat ne nous appartient pas.</summary>
public sealed record VelopackAssetInput(
    string PackageId, string Version, string Type, string FileName,
    string SHA1, string? SHA256, long Size, string? NotesMarkdown);

/// <summary>Dépôt d'une version. <c>ShopIds</c> nul cible toutes les boutiques ; une liste vide
/// dépose sans distribuer, ce qui permet de téléverser puis de décider.</summary>
public sealed record PublishInput(string? Channel, IReadOnlyList<VelopackAssetInput> Assets, IReadOnlyList<Guid>? ShopIds);

public sealed record PromoteInput(string? Channel, IReadOnlyList<Guid>? ShopIds);

public static class UpdateEndpoints
{
    private const string DefaultChannel = "win";

    /// <summary>Répertoire des paquets. En conteneur c'est un volume ; en développement, un
    /// sous-dossier de l'application.</summary>
    public static string StoragePath(IConfiguration config) =>
        Path.GetFullPath(config["Updates:Path"] is { Length: > 0 } path ? path : Path.Combine(AppContext.BaseDirectory, "updates"));

    public static void MapUpdates(this WebApplication app)
    {
        // -------------------------------------------------------------------
        // Côté terminal. Velopack demande releases.{canal}.json puis les fichiers
        // qui y sont nommés, relativement à la même base.
        // -------------------------------------------------------------------

        app.MapGet("/updates/releases.{channel}.json", async (string channel, HttpContext http, ServerDbContext db, CancellationToken ct) =>
        {
            var device = await DeviceAuthentication.ResolveAsync(http, db, ct);
            if (device is null) return Results.Unauthorized();

            var assets = await VisibleAssetsAsync(db, channel, device.ShopId, ct);

            // Sérialisé à la main en PascalCase : la configuration JSON du serveur est en
            // camelCase pour la PWA, et Velopack attend exactement ces noms-là. Une simple
            // différence de casse donnerait un flux vide, donc « aucune mise à jour », sans
            // la moindre erreur visible nulle part.
            var feed = new
            {
                Assets = assets.Select(a => new
                {
                    a.PackageId, a.Version, a.Type, a.FileName,
                    SHA1 = a.Sha1, SHA256 = a.Sha256, a.Size,
                    NotesMarkdown = a.NotesMarkdown ?? string.Empty,
                }).ToArray(),
            };
            return Results.Text(JsonSerializer.Serialize(feed, PascalCase), "application/json");
        });

        app.MapGet("/updates/{fileName}", async (string fileName, HttpContext http, ServerDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            var device = await DeviceAuthentication.ResolveAsync(http, db, ct);
            if (device is null) return Results.Unauthorized();

            // Le nom est vérifié en base avant de toucher au disque : c'est ce qui interdit
            // « ../../appsettings.json » sans avoir à faire confiance à une normalisation de
            // chemin. Et le filtre par boutique s'applique ici aussi — sinon l'échelonnement
            // ne tiendrait qu'au fait que l'autre terminal ignore le nom du fichier.
            var assets = await VisibleAssetsAsync(db, null, device.ShopId, ct);
            var asset = assets.FirstOrDefault(a => a.FileName == fileName);
            if (asset is null) return Results.NotFound();

            var full = Path.Combine(StoragePath(config), asset.FileName);
            if (!File.Exists(full)) return Results.NotFound();
            return Results.File(full, "application/octet-stream", asset.FileName);
        });

        // -------------------------------------------------------------------
        // Côté développeur. Clé distincte de l'authentification de la propriétaire :
        // décider qu'une version part n'est pas une décision de gestion.
        // -------------------------------------------------------------------

        var releases = app.MapGroup("/api/releases").AddEndpointFilter(async (context, next) =>
        {
            var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var expected = config["Admin:ApiKey"];
            // Pas de clé configurée = pas de publication possible. Le contraire — accepter tout
            // le monde tant que rien n'est réglé — est la façon habituelle d'ouvrir une porte.
            if (string.IsNullOrWhiteSpace(expected)) return Results.NotFound();

            var provided = context.HttpContext.Request.Headers["X-Admin-Key"].ToString();
            if (!FixedTimeEquals(provided, expected)) return Results.Unauthorized();
            return await next(context);
        });

        releases.MapPut("/files/{fileName}", async (string fileName, HttpRequest request, IConfiguration config, CancellationToken ct) =>
        {
            if (!IsSafeFileName(fileName)) return Results.BadRequest(new { error = "Nom de fichier invalide." });

            var directory = StoragePath(config);
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, fileName);

            // Écriture par fichier temporaire puis renommage : une CI interrompue à mi-transfert
            // laisserait sinon un paquet tronqué que les terminaux téléchargeraient avec zèle.
            var temporary = destination + ".part";
            await using (var file = File.Create(temporary))
                await request.Body.CopyToAsync(file, ct);
            File.Move(temporary, destination, overwrite: true);

            var info = new FileInfo(destination);
            return Results.Ok(new { fileName, size = info.Length, sha1 = await Sha1Async(destination, ct) });
        });

        // Relecture d'un paquet déjà déposé. Sert à la CI : sans le paquet complet précédent,
        // « vpk pack » ne peut calculer aucun delta et produit un paquet entier à chaque version.
        releases.MapGet("/files/{fileName}", (string fileName, IConfiguration config) =>
        {
            if (!IsSafeFileName(fileName)) return Results.BadRequest(new { error = "Nom de fichier invalide." });
            var path = Path.Combine(StoragePath(config), fileName);
            return File.Exists(path)
                ? Results.File(path, "application/octet-stream", fileName)
                : Results.NotFound();
        });

        releases.MapPost("", async (PublishInput input, ServerDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            var channel = Normalize(input.Channel);
            if (input.Assets.Count == 0) return Results.BadRequest(new { error = "Aucun paquet déclaré." });

            var directory = StoragePath(config);
            foreach (var dto in input.Assets)
            {
                if (!IsSafeFileName(dto.FileName)) return Results.BadRequest(new { error = $"Nom de fichier invalide : {dto.FileName}" });

                var path = Path.Combine(directory, dto.FileName);
                if (!File.Exists(path))
                    return Results.BadRequest(new { error = $"{dto.FileName} n'a pas été téléversé." });

                // La taille annoncée est confrontée au fichier réellement présent : c'est le
                // contrôle qui attrape un transfert coupé, cas bien plus probable qu'une
                // falsification.
                var actual = new FileInfo(path).Length;
                if (actual != dto.Size)
                    return Results.BadRequest(new { error = $"{dto.FileName} fait {actual} octets, {dto.Size} annoncés." });

                var row = await db.ReleaseAssets.SingleOrDefaultAsync(x => x.FileName == dto.FileName, ct);
                if (row is null) { row = new ReleaseAsset { FileName = dto.FileName }; db.ReleaseAssets.Add(row); }
                row.PackageId = dto.PackageId; row.Version = dto.Version; row.Channel = channel;
                row.Type = dto.Type; row.Sha1 = dto.SHA1; row.Sha256 = dto.SHA256; row.Size = dto.Size;
                row.NotesMarkdown = dto.NotesMarkdown; row.IsWithdrawn = false;
            }

            var version = input.Assets[0].Version;
            await SetTargetsAsync(db, channel, version, input.ShopIds, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { version, channel, assets = input.Assets.Count });
        });

        releases.MapPost("/{version}/promote", async (string version, PromoteInput input, ServerDbContext db, CancellationToken ct) =>
        {
            var channel = Normalize(input.Channel);
            if (!await db.ReleaseAssets.AnyAsync(x => x.Channel == channel && x.Version == version, ct))
                return Results.NotFound();

            await SetTargetsAsync(db, channel, version, input.ShopIds, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { version, channel, toutes = input.ShopIds is null, boutiques = input.ShopIds?.Count ?? 0 });
        });

        releases.MapPost("/{version}/withdraw", async (string version, string? channel, ServerDbContext db, CancellationToken ct) =>
        {
            var normalized = Normalize(channel);
            var rows = await db.ReleaseAssets.Where(x => x.Channel == normalized && x.Version == version).ToListAsync(ct);
            if (rows.Count == 0) return Results.NotFound();

            // On retire du flux, on ne supprime pas les fichiers : un terminal peut être en
            // train de télécharger, et une version déjà installée ne se rappelle pas — pour
            // celle-là il faut en republier une plus récente.
            foreach (var row in rows) row.IsWithdrawn = true;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { version, channel = normalized, retires = rows.Count });
        });

        releases.MapGet("", async (ServerDbContext db, CancellationToken ct) =>
        {
            var assets = await db.ReleaseAssets.AsNoTracking().OrderByDescending(x => x.PublishedAt).ToListAsync(ct);
            var targets = await db.ReleaseTargets.AsNoTracking().ToListAsync(ct);
            return Results.Ok(assets
                .GroupBy(a => new { a.Channel, a.Version })
                .Select(g => new
                {
                    g.Key.Channel,
                    g.Key.Version,
                    PublishedAt = g.Min(x => x.PublishedAt),
                    Withdrawn = g.All(x => x.IsWithdrawn),
                    Files = g.Select(x => new { x.FileName, x.Type, x.Size }).ToArray(),
                    Toutes = targets.Any(t => t.Channel == g.Key.Channel && t.Version == g.Key.Version && t.ShopId is null),
                    ShopIds = targets.Where(t => t.Channel == g.Key.Channel && t.Version == g.Key.Version && t.ShopId is not null)
                        .Select(t => t.ShopId!.Value).ToArray(),
                })
                .OrderByDescending(x => x.PublishedAt));
        });
    }

    /// <summary>Paquets qu'une boutique donnée a le droit de voir. Un canal nul les prend tous —
    /// utile pour résoudre un nom de fichier dont on ne connaît pas le canal.</summary>
    private static async Task<List<ReleaseAsset>> VisibleAssetsAsync(
        ServerDbContext db, string? channel, Guid shopId, CancellationToken ct)
    {
        var query = db.ReleaseAssets.AsNoTracking().Where(x => !x.IsWithdrawn);
        if (channel is not null) query = query.Where(x => x.Channel == channel);

        return await query
            .Where(asset => db.ReleaseTargets.Any(t =>
                t.Channel == asset.Channel && t.Version == asset.Version &&
                (t.ShopId == null || t.ShopId == shopId)))
            .OrderByDescending(x => x.PublishedAt)
            .ToListAsync(ct);
    }

    /// <summary>Remplace le ciblage d'une version. <c>shopIds</c> nul = toutes les boutiques.</summary>
    private static async Task SetTargetsAsync(
        ServerDbContext db, string channel, string version, IReadOnlyList<Guid>? shopIds, CancellationToken ct)
    {
        var existing = await db.ReleaseTargets.Where(x => x.Channel == channel && x.Version == version).ToListAsync(ct);
        db.ReleaseTargets.RemoveRange(existing);

        if (shopIds is null)
            db.ReleaseTargets.Add(new ReleaseTarget { Channel = channel, Version = version, ShopId = null });
        else
            foreach (var shopId in shopIds.Distinct())
                db.ReleaseTargets.Add(new ReleaseTarget { Channel = channel, Version = version, ShopId = shopId });
    }

    private static readonly JsonSerializerOptions PascalCase = new() { PropertyNamingPolicy = null };

    private static string Normalize(string? channel) =>
        string.IsNullOrWhiteSpace(channel) ? DefaultChannel : channel.Trim().ToLowerInvariant();

    /// <summary>Un nom de fichier, pas un chemin. Refuse tout séparateur et tout « .. ».</summary>
    private static bool IsSafeFileName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName.Length <= 200
        && fileName == Path.GetFileName(fileName)
        && !fileName.Contains("..", StringComparison.Ordinal)
        && fileName.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');

    private static bool FixedTimeEquals(string provided, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided.PadRight(expected.Length)[..expected.Length]),
            Encoding.UTF8.GetBytes(expected))
        && provided.Length == expected.Length;

    private static async Task<string> Sha1Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA1.HashDataAsync(stream, ct));
    }
}
