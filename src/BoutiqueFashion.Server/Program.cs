using BoutiqueFashion.Contracts;
using BoutiqueFashion.Server.Data;
using BoutiqueFashion.Server.Sync;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ServerDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")
        ?? "Host=localhost;Port=5432;Database=boutique;Username=boutique;Password=boutique"));
builder.Services.AddScoped<SyncApplier>();
builder.Services.AddProblemDetails();

var app = builder.Build();

// Migration au démarrage : le serveur est déployé en conteneur et la base est la sienne. Une
// étape manuelle de plus serait une étape oubliée un soir de mise à jour.
if (!app.Environment.IsEnvironment("Testing"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ServerDbContext>().Database.MigrateAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// ---------------------------------------------------------------------------
// Pilotage — création des boutiques et des codes d'appairage.
// ---------------------------------------------------------------------------

var admin = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
    AdminAuthentication.IsAuthorized(context.HttpContext, context.HttpContext.RequestServices.GetRequiredService<IConfiguration>())
        ? await next(context)
        : Results.Unauthorized());

admin.MapPost("/shops", async (ShopInput input, ServerDbContext db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "Le nom de la boutique est obligatoire." });
    var shop = new Shop { Name = input.Name.Trim(), City = input.City, Address = input.Address, Phone = input.Phone };
    db.Shops.Add(shop);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/shops/{shop.Id}", shop);
});

admin.MapGet("/shops", async (ServerDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Shops.OrderBy(x => x.Name)
        .Select(x => new
        {
            x.Id, x.Name, x.City, x.IsActive,
            Devices = db.Devices.Count(d => d.ShopId == x.Id && d.RevokedAt == null),
            LastSeenAt = db.Devices.Where(d => d.ShopId == x.Id).Max(d => (DateTimeOffset?)d.LastSeenAt),
        })
        .ToListAsync(ct)));

admin.MapPost("/shops/{shopId:guid}/enrollment-codes", async (Guid shopId, ServerDbContext db, CancellationToken ct) =>
{
    if (!await db.Shops.AnyAsync(x => x.Id == shopId, ct)) return Results.NotFound();
    var code = new EnrollmentCode
    {
        Code = DeviceTokens.CreateEnrollmentCode(),
        ShopId = shopId,
        // Assez pour installer un terminal dans la journée, trop peu pour qu'un code oublié
        // sur un bout de papier serve encore la semaine suivante.
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
    };
    db.EnrollmentCodes.Add(code);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { code.Code, code.ExpiresAt });
});

admin.MapDelete("/devices/{deviceId:guid}", async (Guid deviceId, ServerDbContext db, CancellationToken ct) =>
{
    var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId, ct);
    if (device is null) return Results.NotFound();
    device.RevokedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

// ---------------------------------------------------------------------------
// Référentiel — autorité serveur. Chaque écriture avance le curseur, ce qui suffit
// à faire redescendre la modification sur tous les terminaux au prochain passage.
// ---------------------------------------------------------------------------

admin.MapPut("/catalog", async (CatalogInput input, ServerDbContext db, CancellationToken ct) =>
{
    foreach (var dto in input.Categories ?? [])
    {
        var row = await db.Categories.SingleOrDefaultAsync(x => x.Id == dto.Id, ct);
        if (row is null) { row = new Category { Id = dto.Id }; db.Categories.Add(row); }
        row.Name = dto.Name; row.IsActive = dto.IsActive;
        row.Seq = await db.NextSeqAsync(ct);
    }
    foreach (var dto in input.Products ?? [])
    {
        var row = await db.Products.SingleOrDefaultAsync(x => x.Id == dto.Id, ct);
        if (row is null) { row = new Product { Id = dto.Id }; db.Products.Add(row); }
        row.CategoryId = dto.CategoryId; row.Name = dto.Name; row.Brand = dto.Brand; row.Description = dto.Description;
        row.SubCategory = dto.SubCategory; row.Gender = dto.Gender; row.Season = dto.Season;
        row.Type = dto.Type; row.IsActive = dto.IsActive;
        row.Seq = await db.NextSeqAsync(ct);
    }
    foreach (var dto in input.Variants ?? [])
    {
        var row = await db.Variants.SingleOrDefaultAsync(x => x.Id == dto.Id, ct);
        if (row is null) { row = new Variant { Id = dto.Id }; db.Variants.Add(row); }
        row.ProductId = dto.ProductId; row.Sku = dto.Sku; row.Barcode = dto.Barcode; row.Size = dto.Size;
        row.Color = dto.Color; row.Material = dto.Material; row.Supplier = dto.Supplier;
        row.CostXof = dto.CostXof; row.PriceXof = dto.PriceXof; row.PromotionalPriceXof = dto.PromotionalPriceXof;
        row.PromotionStartsAt = dto.PromotionStartsAt; row.PromotionEndsAt = dto.PromotionEndsAt;
        row.LowStockThreshold = dto.LowStockThreshold; row.IsActive = dto.IsActive;
        row.Seq = await db.NextSeqAsync(ct);
    }
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

admin.MapPut("/shops/{shopId:guid}/settings", async (Guid shopId, IReadOnlyList<SettingDto> settings, ServerDbContext db, CancellationToken ct) =>
{
    if (!await db.Shops.AnyAsync(x => x.Id == shopId, ct)) return Results.NotFound();
    foreach (var dto in settings)
    {
        var row = await db.ShopSettings.SingleOrDefaultAsync(x => x.ShopId == shopId && x.Key == dto.Key, ct);
        if (row is null) { row = new ShopSetting { ShopId = shopId, Key = dto.Key }; db.ShopSettings.Add(row); }
        row.Value = dto.Value;
        row.Seq = await db.NextSeqAsync(ct);
    }
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

admin.MapGet("/shops/{shopId:guid}/stock", async (Guid shopId, ServerDbContext db, CancellationToken ct) =>
    Results.Ok(await db.ShopStocks.AsNoTracking().Where(x => x.ShopId == shopId)
        .Join(db.Variants, s => s.VariantId, v => v.Id, (s, v) => new { v.Sku, s.QuantityOnHand, s.QuantityReserved, Available = s.QuantityOnHand - s.QuantityReserved })
        .OrderBy(x => x.Sku).ToListAsync(ct)));

// ---------------------------------------------------------------------------
// Appairage — anonyme, mais le code est à usage unique et expire.
// ---------------------------------------------------------------------------

app.MapPost("/api/devices/enroll", async (EnrollRequest request, ServerDbContext db, CancellationToken ct) =>
{
    var normalized = (request.Code ?? string.Empty).Trim().ToUpperInvariant();
    var code = await db.EnrollmentCodes.Include(x => x.Shop).SingleOrDefaultAsync(x => x.Code == normalized, ct);

    // Message uniforme : distinguer « code inconnu » de « code expiré » aiderait surtout
    // quelqu'un qui essaie des codes au hasard.
    if (code is null || code.UsedAt is not null || code.ExpiresAt < DateTimeOffset.UtcNow || code.Shop is null)
        return Results.BadRequest(new { error = "Code d'appairage invalide ou expiré." });

    var token = DeviceTokens.Create();
    var device = new Device
    {
        ShopId = code.ShopId,
        Name = string.IsNullOrWhiteSpace(request.DeviceName) ? "Terminal" : request.DeviceName.Trim(),
        TokenHash = DeviceTokens.Hash(token),
        LastSeenAt = DateTimeOffset.UtcNow,
    };
    db.Devices.Add(device);
    code.UsedAt = DateTimeOffset.UtcNow;
    code.UsedByDeviceId = device.Id;
    await db.SaveChangesAsync(ct);

    // Le jeton n'est montré qu'ici : seule son empreinte est conservée.
    return Results.Ok(new EnrollResponse(code.ShopId, code.Shop.Name, device.Id, token));
});

// ---------------------------------------------------------------------------
// Synchronisation — réservée aux terminaux appairés.
// ---------------------------------------------------------------------------

app.MapPost("/api/sync/push", async (SyncPushRequest request, HttpContext http, ServerDbContext db, SyncApplier applier, CancellationToken ct) =>
{
    var device = await DeviceAuthentication.ResolveAsync(http, db, ct);
    if (device is null) return Results.Unauthorized();
    if (request.Events.Count == 0) return Results.Ok(new SyncPushResponse([], []));
    return Results.Ok(await applier.ApplyAsync(device.ShopId, request.Events, ct));
});

app.MapGet("/api/sync/pull", async (long since, HttpContext http, ServerDbContext db, CancellationToken ct) =>
{
    var device = await DeviceAuthentication.ResolveAsync(http, db, ct);
    if (device is null) return Results.Unauthorized();

    // Plafond par appel : une première synchronisation ne doit pas immobiliser la caisse le
    // temps de descendre tout le catalogue. HasMore invite le terminal à repasser.
    const int PageSize = 500;

    var categories = await db.Categories.AsNoTracking().Where(x => x.Seq > since).OrderBy(x => x.Seq).Take(PageSize)
        .Select(x => new CategoryDto(x.Id, x.Name, x.IsActive)).ToListAsync(ct);
    var products = await db.Products.AsNoTracking().Where(x => x.Seq > since).OrderBy(x => x.Seq).Take(PageSize)
        .Select(x => new ProductDto(x.Id, x.CategoryId, x.Name, x.Brand, x.Description, x.SubCategory, x.Gender, x.Season, x.Type, x.IsActive)).ToListAsync(ct);
    var variants = await db.Variants.AsNoTracking().Where(x => x.Seq > since).OrderBy(x => x.Seq).Take(PageSize)
        .Select(x => new VariantDto(x.Id, x.ProductId, x.Sku, x.Barcode, x.Size, x.Color, x.Material, x.Supplier, x.CostXof, x.PriceXof, x.PromotionalPriceXof, x.PromotionStartsAt, x.PromotionEndsAt, x.LowStockThreshold, x.IsActive)).ToListAsync(ct);
    var settings = await db.ShopSettings.AsNoTracking().Where(x => x.ShopId == device.ShopId && x.Seq > since).OrderBy(x => x.Seq).Take(PageSize)
        .Select(x => new SettingDto(x.Key, x.Value)).ToListAsync(ct);

    // Le curseur n'avance que jusqu'au plus petit reste : avancer plus loin ferait sauter
    // définitivement les lignes des autres tables restées derrière.
    var highest = new[]
    {
        await Ceiling(db.Categories.Where(x => x.Seq > since).Select(x => x.Seq), categories.Count, PageSize, ct),
        await Ceiling(db.Products.Where(x => x.Seq > since).Select(x => x.Seq), products.Count, PageSize, ct),
        await Ceiling(db.Variants.Where(x => x.Seq > since).Select(x => x.Seq), variants.Count, PageSize, ct),
        await Ceiling(db.ShopSettings.Where(x => x.ShopId == device.ShopId && x.Seq > since).Select(x => x.Seq), settings.Count, PageSize, ct),
    };
    var truncated = highest.Where(x => x is not null).Select(x => x!.Value).ToArray();
    var cursor = truncated.Length > 0 ? truncated.Min() : await MaxSeqAsync(db, device.ShopId, since, ct);

    return Results.Ok(new SyncPullResponse(cursor, categories, products, variants, settings, truncated.Length > 0));

    static async Task<long?> Ceiling(IQueryable<long> sequences, int returned, int pageSize, CancellationToken ct) =>
        returned < pageSize ? null : await sequences.OrderBy(x => x).Skip(pageSize - 1).FirstAsync(ct);

    static async Task<long> MaxSeqAsync(ServerDbContext db, Guid shopId, long since, CancellationToken ct)
    {
        var candidates = new[]
        {
            await db.Categories.MaxAsync(x => (long?)x.Seq, ct) ?? 0,
            await db.Products.MaxAsync(x => (long?)x.Seq, ct) ?? 0,
            await db.Variants.MaxAsync(x => (long?)x.Seq, ct) ?? 0,
            await db.ShopSettings.Where(x => x.ShopId == shopId).MaxAsync(x => (long?)x.Seq, ct) ?? 0,
        };
        return Math.Max(since, candidates.Max());
    }
});

app.Run();

internal sealed record ShopInput(string Name, string? City, string? Address, string? Phone);

internal sealed record CatalogInput(
    IReadOnlyList<CategoryDto>? Categories,
    IReadOnlyList<ProductDto>? Products,
    IReadOnlyList<VariantDto>? Variants);

/// <summary>Point d'entrée exposé pour les tests d'intégration.</summary>
public partial class Program;
