using BoutiqueFashion.Contracts;
using BoutiqueFashion.Server.Data;
using BoutiqueFashion.Server.Endpoints;
using BoutiqueFashion.Server.Notifications;
using BoutiqueFashion.Server.Sync;
using Microsoft.EntityFrameworkCore;

// Génération des clés VAPID en ligne de commande : sans elle il faudrait un outil externe
// pour configurer les notifications, ce qui garantit qu'on ne le fera jamais.
if (args is ["vapid", ..])
{
    var (publicKey, privateKey) = Vapid.GenerateKeys();
    Console.WriteLine($"Vapid__PublicKey={publicKey}");
    Console.WriteLine($"Vapid__PrivateKey={privateKey}");
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Une seule source pour la chaîne de connexion : la configuration. Le repli codé en dur
// qui vivait ici était mort — appsettings.json en fournit toujours une — et il aurait
// silencieusement branché la production sur une base locale le jour où la variable
// d'environnement aurait manqué.
builder.Services.AddDbContext<ServerDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException(
            "ConnectionStrings__Postgres n'est pas configurée.")));
builder.Services.AddScoped<SyncApplier>();
builder.Services.AddScoped<Notifier>();
builder.Services.AddHttpClient("openwa");
builder.Services.AddHttpClient("webpush");
builder.Services.AddProblemDetails();

// En production, l'application et l'API partagent le même domaine derrière Caddy : aucune
// requête n'est croisée et rien de tout ceci n'existe. En développement, le serveur de Next
// tourne sur un autre port, et sans cette autorisation on ne peut pas travailler sur l'interface.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
        .WithOrigins("http://localhost:3000", "http://127.0.0.1:3000")
        .AllowAnyHeader()
        .AllowAnyMethod()));
}

var app = builder.Build();

// Migration au démarrage : le serveur est déployé en conteneur et la base est la sienne. Une
// étape manuelle de plus serait une étape oubliée un soir de mise à jour.
if (!app.Environment.IsEnvironment("Testing"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
    await db.Database.MigrateAsync();
    await UserAuthentication.EnsureFirstUserAsync(db, app.Configuration);
}

if (app.Environment.IsDevelopment()) app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapShowcase();

// ---------------------------------------------------------------------------
// Comptes de pilotage.
// ---------------------------------------------------------------------------

app.MapPost("/api/auth/login", async (LoginInput input, ServerDbContext db, CancellationToken ct) =>
{
    var result = await UserAuthentication.LoginAsync(db, input.Username, input.Password, ct);
    // Réponse unique quel que soit le motif — compte inexistant, mot de passe faux, compte
    // verrouillé : détailler aiderait surtout celui qui essaie des identifiants au hasard.
    return result is null
        ? Results.Json(new { error = "Identifiant ou mot de passe incorrect." }, statusCode: StatusCodes.Status401Unauthorized)
        : Results.Ok(new { token = result.Value.Token, expiresAt = result.Value.ExpiresAt, displayName = result.Value.User.DisplayName, username = result.Value.User.Username });
});

// ---------------------------------------------------------------------------
// Pilotage — réservé aux comptes authentifiés.
// ---------------------------------------------------------------------------

var admin = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
{
    var db = context.HttpContext.RequestServices.GetRequiredService<ServerDbContext>();
    var user = await UserAuthentication.ResolveAsync(context.HttpContext, db, context.HttpContext.RequestAborted);
    if (user is null) return Results.Unauthorized();
    context.HttpContext.Items["user"] = user;
    return await next(context);
});

admin.MapReporting();
admin.MapOrders();

// ---------------------------------------------------------------------------
// Alertes. WhatsApp porte le détail, la notification web ne fait que réveiller
// l'application — voir Notifier pour la raison.
// ---------------------------------------------------------------------------

admin.MapGet("/notifications/settings", async (ServerDbContext db, IConfiguration config, CancellationToken ct) =>
{
    var settings = await db.NotificationSettings.FirstOrDefaultAsync(ct) ?? new NotificationSettings();
    return Results.Ok(new
    {
        settings.WhatsAppNumber, settings.OnCashOpened, settings.OnCashClosed,
        settings.OnCashVariance, settings.OnNewOrder,
        // La clé publique est nécessaire au navigateur pour s'abonner ; son absence indique
        // que les notifications web ne sont pas configurées sur ce serveur.
        VapidPublicKey = config["Vapid:PublicKey"],
        WhatsAppConfigured = !string.IsNullOrWhiteSpace(config["OpenWa:BaseUrl"]),
        Subscriptions = await db.PushSubscriptions.CountAsync(ct),
    });
});

admin.MapPut("/notifications/settings", async (NotificationSettingsInput input, ServerDbContext db, CancellationToken ct) =>
{
    var settings = await db.NotificationSettings.FirstOrDefaultAsync(ct);
    if (settings is null) { settings = new NotificationSettings(); db.NotificationSettings.Add(settings); }
    // Seuls les chiffres : un numéro copié depuis un carnet arrive avec des espaces et un « + ».
    settings.WhatsAppNumber = string.IsNullOrWhiteSpace(input.WhatsAppNumber)
        ? null
        : new string([.. input.WhatsAppNumber.Where(char.IsDigit)]);
    settings.OnCashOpened = input.OnCashOpened;
    settings.OnCashClosed = input.OnCashClosed;
    settings.OnCashVariance = input.OnCashVariance;
    settings.OnNewOrder = input.OnNewOrder;
    settings.UpdatedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

admin.MapPost("/notifications/subscriptions", async (PushSubscriptionInput input, HttpContext http, ServerDbContext db, CancellationToken ct) =>
{
    var user = (UserContext)http.Items["user"]!;
    var existing = await db.PushSubscriptions.SingleOrDefaultAsync(x => x.Endpoint == input.Endpoint, ct);
    if (existing is null)
        db.PushSubscriptions.Add(new PushSubscription { UserId = user.UserId, Endpoint = input.Endpoint, P256dh = input.P256dh, Auth = input.Auth, Label = input.Label });
    else { existing.P256dh = input.P256dh; existing.Auth = input.Auth; existing.UserId = user.UserId; }
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

admin.MapDelete("/notifications/subscriptions", async (string endpoint, ServerDbContext db, CancellationToken ct) =>
{
    var existing = await db.PushSubscriptions.SingleOrDefaultAsync(x => x.Endpoint == endpoint, ct);
    if (existing is not null) { db.PushSubscriptions.Remove(existing); await db.SaveChangesAsync(ct); }
    return Results.NoContent();
});

admin.MapPost("/notifications/test", async (Notifier notifier, CancellationToken ct) =>
{
    // Un bouton d'essai vaut mieux qu'une configuration qu'on croit bonne : c'est le seul moyen
    // de savoir que le message arrive vraiment sur le bon téléphone.
    await notifier.SendAsync(new Alert(NotificationKind.CashOpened, "Bana Shop", "Message de test. Vos alertes sont bien configurées."), ct);
    return Results.NoContent();
});

admin.MapGet("/auth/me", (HttpContext http) =>
{
    var user = (UserContext)http.Items["user"]!;
    return Results.Ok(new { user.Username, user.DisplayName });
});

admin.MapPost("/auth/logout", async (HttpContext http, ServerDbContext db, CancellationToken ct) =>
{
    var header = http.Request.Headers.Authorization.ToString();
    var hash = Passwords.HashToken(header[UserAuthentication.Scheme.Length..].Trim());
    var session = await db.UserSessions.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
    if (session is not null) { session.RevokedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); }
    return Results.NoContent();
});

admin.MapPost("/auth/password", async (PasswordChangeInput input, HttpContext http, ServerDbContext db, CancellationToken ct) =>
{
    var context = (UserContext)http.Items["user"]!;
    var user = await db.Users.SingleAsync(x => x.Id == context.UserId, ct);
    if (!Passwords.Verify(input.CurrentPassword, user.PasswordHash))
        return Results.BadRequest(new { error = "Mot de passe actuel incorrect." });

    // Validation traduite en réponse plutôt qu'en exception : un mot de passe trop court est
    // une saisie à corriger, pas une panne du serveur.
    try { Passwords.Validate(input.NewPassword); }
    catch (ArgumentException e) { return Results.BadRequest(new { error = e.Message }); }
    user.PasswordHash = Passwords.Hash(input.NewPassword);

    // Toutes les autres sessions tombent : changer son mot de passe doit déconnecter l'appareil
    // qu'on soupçonne, sinon le geste ne sert à rien.
    var others = await db.UserSessions.Where(x => x.UserId == user.Id).ToListAsync(ct);
    db.UserSessions.RemoveRange(others);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

admin.MapPost("/shops", async (ShopInput input, ServerDbContext db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "Le nom de la boutique est obligatoire." });
    var shop = new Shop { Name = input.Name.Trim(), City = input.City, Address = input.Address, Phone = input.Phone, Hours = input.Hours };
    db.Shops.Add(shop);
    await MirrorToTerminalAsync(db, shop, ct);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/shops/{shop.Id}", shop);
});

admin.MapPut("/shops/{shopId:guid}", async (Guid shopId, ShopInput input, ServerDbContext db, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(input.Name)) return Results.BadRequest(new { error = "Le nom de la boutique est obligatoire." });
    var shop = await db.Shops.SingleOrDefaultAsync(x => x.Id == shopId, ct);
    if (shop is null) return Results.NotFound();

    shop.Name = input.Name.Trim();
    shop.City = Trim(input.City);
    shop.Address = Trim(input.Address);
    shop.Phone = Trim(input.Phone);
    shop.Hours = Trim(input.Hours);
    await MirrorToTerminalAsync(db, shop, ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(shop);

    static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
});

// L'identité de la boutique s'écrit ici et se recopie dans ses réglages, qui descendent aux
// terminaux et impriment les tickets. Sans cette recopie, corriger une adresse changerait le
// site sans changer le ticket — ou l'inverse — et rien ne le dirait.
static async Task MirrorToTerminalAsync(ServerDbContext db, Shop shop, CancellationToken ct)
{
    var valeurs = new Dictionary<string, string?>
    {
        ["Shop.Name"] = shop.Name,
        ["Shop.Address"] = shop.Address,
        ["Shop.Phone"] = shop.Phone,
    };

    foreach (var (key, value) in valeurs)
    {
        var row = await db.ShopSettings.SingleOrDefaultAsync(x => x.ShopId == shop.Id && x.Key == key, ct);
        if (row is null)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            row = new ShopSetting { ShopId = shop.Id, Key = key };
            db.ShopSettings.Add(row);
        }
        row.Value = value ?? string.Empty;
        // Le curseur avance : c'est ce qui fait redescendre la correction au prochain cycle.
        row.Seq = await db.NextSeqAsync(ct);
    }
}

// ---------------------------------------------------------------------------
// Textes du site vitrine. Réglables sans redéploiement : une année d'ouverture
// ou une accroche n'ont aucune raison d'exiger une mise en production.
// ---------------------------------------------------------------------------

admin.MapGet("/site-settings", async (ServerDbContext db, CancellationToken ct) =>
    Results.Ok(await db.SiteSettings.AsNoTracking().OrderBy(x => x.Key)
        .Select(x => new SettingDto(x.Key, x.Value)).ToListAsync(ct)));

admin.MapPut("/site-settings", async (IReadOnlyList<SettingDto> settings, ServerDbContext db, CancellationToken ct) =>
{
    foreach (var dto in settings)
    {
        var row = await db.SiteSettings.SingleOrDefaultAsync(x => x.Key == dto.Key, ct);
        if (row is null) { row = new SiteSetting { Key = dto.Key }; db.SiteSettings.Add(row); }
        row.Value = dto.Value;
        row.UpdatedAt = DateTimeOffset.UtcNow;
    }
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
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

admin.MapGet("/catalog", async (ServerDbContext db, CancellationToken ct) => Results.Ok(new
{
    Categories = await db.Categories.AsNoTracking().OrderBy(x => x.Name)
        .Select(x => new CategoryDto(x.Id, x.Name, x.IsActive)).ToListAsync(ct),
    Products = await db.Products.AsNoTracking().OrderBy(x => x.Name)
        .Select(x => new ProductDto(x.Id, x.CategoryId, x.Name, x.Brand, x.Description, x.SubCategory, x.Gender, x.Season, x.Type, x.IsActive, x.ShopId)).ToListAsync(ct),
    Variants = await db.Variants.AsNoTracking().OrderBy(x => x.Sku)
        .Select(x => new VariantDto(x.Id, x.ProductId, x.Sku, x.Barcode, x.Size, x.Color, x.Material, x.Supplier, x.CostXof, x.PriceXof, x.PromotionalPriceXof, x.PromotionStartsAt, x.PromotionEndsAt, x.LowStockThreshold, x.IsActive)).ToListAsync(ct),
}));

admin.MapGet("/shops/{shopId:guid}/devices", async (Guid shopId, ServerDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Devices.AsNoTracking().Where(x => x.ShopId == shopId)
        .OrderByDescending(x => x.LastSeenAt)
        .Select(x => new
        {
            x.Id, x.Name, x.CreatedAt, x.LastSeenAt, Revoked = x.RevokedAt != null,
            x.AppVersion, x.AppVersionSince, x.PendingVersion, x.UpdateError,
        })
        .ToListAsync(ct)));

admin.MapGet("/shops/{shopId:guid}/settings", async (Guid shopId, ServerDbContext db, CancellationToken ct) =>
    Results.Ok(await db.ShopSettings.AsNoTracking().Where(x => x.ShopId == shopId).OrderBy(x => x.Key)
        .Select(x => new SettingDto(x.Key, x.Value)).ToListAsync(ct)));

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
        if (dto.ShopId is { } scope && !await db.Shops.AnyAsync(x => x.Id == scope, ct))
            return Results.BadRequest(new { error = $"Boutique {scope} inconnue." });

        var row = await db.Products.SingleOrDefaultAsync(x => x.Id == dto.Id, ct);
        if (row is null) { row = new Product { Id = dto.Id }; db.Products.Add(row); }
        row.CategoryId = dto.CategoryId; row.Name = dto.Name; row.Brand = dto.Brand; row.Description = dto.Description;
        row.SubCategory = dto.SubCategory; row.Gender = dto.Gender; row.Season = dto.Season;
        row.Type = dto.Type; row.IsActive = dto.IsActive;
        // null = catalogue global, présent partout ; renseigné = exclusif à cette boutique.
        row.ShopId = dto.ShopId;
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

    // Un terminal ne reçoit que le catalogue global et celui de sa propre boutique.
    var visible = db.Products.AsNoTracking().Where(x => x.ShopId == null || x.ShopId == device.ShopId);

    var categories = await db.Categories.AsNoTracking().Where(x => x.Seq > since).OrderBy(x => x.Seq).Take(PageSize)
        .Select(x => new CategoryDto(x.Id, x.Name, x.IsActive)).ToListAsync(ct);
    var products = await visible.Where(x => x.Seq > since).OrderBy(x => x.Seq).Take(PageSize)
        .Select(x => new ProductDto(x.Id, x.CategoryId, x.Name, x.Brand, x.Description, x.SubCategory, x.Gender, x.Season, x.Type, x.IsActive, x.ShopId)).ToListAsync(ct);
    var variants = await db.Variants.AsNoTracking()
        .Where(x => x.Seq > since && visible.Any(p => p.Id == x.ProductId)).OrderBy(x => x.Seq).Take(PageSize)
        .Select(x => new VariantDto(x.Id, x.ProductId, x.Sku, x.Barcode, x.Size, x.Color, x.Material, x.Supplier, x.CostXof, x.PriceXof, x.PromotionalPriceXof, x.PromotionStartsAt, x.PromotionEndsAt, x.LowStockThreshold, x.IsActive)).ToListAsync(ct);
    var settings = await db.ShopSettings.AsNoTracking().Where(x => x.ShopId == device.ShopId && x.Seq > since).OrderBy(x => x.Seq).Take(PageSize)
        .Select(x => new SettingDto(x.Key, x.Value)).ToListAsync(ct);

    // Le filtre ci-dessus cesse d'envoyer un article dont la portée s'est resserrée ailleurs :
    // sans cette liste, le terminal en garderait une copie fantôme, vendable et invisible du
    // serveur.
    // Les commandes de cette boutique, dans leur état courant. Elles descendent par le même
    // curseur que le référentiel : une annulation décidée depuis le téléphone atteint donc la
    // caisse au prochain cycle, sans mécanisme séparé.
    var orders = await db.Orders.AsNoTracking()
        .Where(x => x.ShopId == device.ShopId && x.Seq > since)
        .OrderBy(x => x.Seq).Take(PageSize)
        .Select(x => new OrderDto(
            x.Id, x.Number, x.CustomerName, x.Phone, x.Note, x.Channel, x.Status, x.TotalXof, x.SaleId, x.CreatedAt,
            x.Lines.Select(l => new OrderLineDto(l.VariantId, l.Sku, l.Description, l.Quantity, l.UnitPriceXof)).ToList()))
        .ToListAsync(ct);

    var retired = await db.Products.AsNoTracking()
        .Where(x => x.Seq > since && x.ShopId != null && x.ShopId != device.ShopId)
        .OrderBy(x => x.Seq).Take(PageSize).Select(x => x.Id).ToListAsync(ct);

    // Le curseur n'avance que jusqu'au plus petit reste : avancer plus loin ferait sauter
    // définitivement les lignes des autres tables restées derrière.
    var highest = new[]
    {
        await Ceiling(db.Categories.Where(x => x.Seq > since).Select(x => x.Seq), categories.Count, PageSize, ct),
        await Ceiling(visible.Where(x => x.Seq > since).Select(x => x.Seq), products.Count, PageSize, ct),
        await Ceiling(db.Variants.Where(x => x.Seq > since && visible.Any(p => p.Id == x.ProductId)).Select(x => x.Seq), variants.Count, PageSize, ct),
        await Ceiling(db.ShopSettings.Where(x => x.ShopId == device.ShopId && x.Seq > since).Select(x => x.Seq), settings.Count, PageSize, ct),
        await Ceiling(db.Products.Where(x => x.Seq > since && x.ShopId != null && x.ShopId != device.ShopId).Select(x => x.Seq), retired.Count, PageSize, ct),
        await Ceiling(db.Orders.Where(x => x.ShopId == device.ShopId && x.Seq > since).Select(x => x.Seq), orders.Count, PageSize, ct),
    };
    var truncated = highest.Where(x => x is not null).Select(x => x!.Value).ToArray();
    var cursor = truncated.Length > 0 ? truncated.Min() : await MaxSeqAsync(db, device.ShopId, since, ct);

    return Results.Ok(new SyncPullResponse(cursor, categories, products, variants, settings, truncated.Length > 0, retired, orders));

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
            await db.Orders.Where(x => x.ShopId == shopId).MaxAsync(x => (long?)x.Seq, ct) ?? 0,
        };
        return Math.Max(since, candidates.Max());
    }
});

// ---------------------------------------------------------------------------
// Publication logicielle (lot 5) : flux Velopack pour les terminaux, dépôt et
// ciblage pour le développeur.
// ---------------------------------------------------------------------------

app.MapUpdates();

// Battement de cœur du terminal, envoyé à chaque cycle de synchronisation. C'est la seule
// source d'information sur ce qui tourne réellement dans une boutique : sans elle, « est-ce que
// la mise à jour est passée ? » se répond en téléphonant.
app.MapPost("/api/devices/status", async (DeviceStatusRequest input, HttpContext http, ServerDbContext db, CancellationToken ct) =>
{
    var context = await DeviceAuthentication.ResolveAsync(http, db, ct);
    if (context is null) return Results.Unauthorized();

    var device = await db.Devices.SingleAsync(x => x.Id == context.DeviceId, ct);
    // La date ne bouge qu'au changement de version : c'est « depuis quand cette boutique tourne
    // en 1.4.2 », pas « quand a-t-elle parlé pour la dernière fois » — LastSeenAt le dit déjà.
    if (device.AppVersion != input.AppVersion)
    {
        device.AppVersion = input.AppVersion;
        device.AppVersionSince = DateTimeOffset.UtcNow;
    }
    device.PendingVersion = input.PendingVersion;
    device.UpdateError = input.UpdateError;
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

app.Run();

internal sealed record ShopInput(string Name, string? City, string? Address, string? Phone, string? Hours);
internal sealed record LoginInput(string Username, string Password);
internal sealed record NotificationSettingsInput(string? WhatsAppNumber, bool OnCashOpened, bool OnCashClosed, bool OnCashVariance, bool OnNewOrder);
internal sealed record PushSubscriptionInput(string Endpoint, string P256dh, string Auth, string? Label);
internal sealed record PasswordChangeInput(string CurrentPassword, string NewPassword);

internal sealed record CatalogInput(
    IReadOnlyList<CategoryDto>? Categories,
    IReadOnlyList<ProductDto>? Products,
    IReadOnlyList<VariantDto>? Variants);

/// <summary>Point d'entrée exposé pour les tests d'intégration.</summary>
public partial class Program;
