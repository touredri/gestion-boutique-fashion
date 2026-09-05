using System.Net.Http.Json;
using System.Text.Json;
using BoutiqueFashion.Application;
using BoutiqueFashion.Contracts;
using BoutiqueFashion.Domain;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Infrastructure;

/// <summary>
/// Agent de synchronisation du terminal.
///
/// Règle qui prime sur toutes les autres : <b>la caisse n'attend jamais le réseau</b>. Aucune
/// méthode ne lève, aucune n'est appelée sur un chemin critique. Hors ligne, la file grossit et
/// la boutique continue de vendre ; au retour du réseau, tout remonte dans l'ordre.
///
/// La boutique n'est jamais nommée dans une requête : elle est déduite du jeton d'appareil côté
/// serveur. Un terminal ne peut donc pas, même par erreur de programmation, écrire chez une autre.
/// </summary>
public sealed class SyncService(
    IDbContextFactory<BoutiqueDbContext> factory,
    IAuthorizationService authorization) : ISyncService, IDisposable
{
    public const string ServerUrlKey = "Sync.ServerUrl";
    public const string TokenKey = "Sync.DeviceToken";
    public const string ShopIdKey = "Sync.ShopId";
    public const string ShopNameKey = "Sync.ShopName";
    public const string CursorKey = "Sync.Cursor";

    /// <summary>Lots bornés : une boutique longtemps hors ligne ne doit pas tenter d'envoyer
    /// trois mois de ventes en une requête que le serveur refuserait pour cause de taille.</summary>
    private const int BatchSize = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim gate = new(1, 1);

    private DateTimeOffset? lastSuccessAt;
    private string? lastError;
    private volatile bool isRunning;

    public void Dispose() { http.Dispose(); gate.Dispose(); }

    // --- État --------------------------------------------------------------

    public async Task<SyncState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await BuildStateAsync(db, cancellationToken);
    }

    private async Task<SyncState> BuildStateAsync(BoutiqueDbContext db, CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(db, cancellationToken);
        var pending = await db.SyncOutbox.CountAsync(x => x.SentAt == null, cancellationToken);
        return new SyncState(
            settings.TryGetValue(TokenKey, out var token) && !string.IsNullOrEmpty(token),
            settings.GetValueOrDefault(ShopNameKey),
            pending, lastSuccessAt, lastError, isRunning);
    }

    private static async Task<Dictionary<string, string>> ReadSettingsAsync(BoutiqueDbContext db, CancellationToken cancellationToken)
    {
        var keys = new[] { ServerUrlKey, TokenKey, ShopIdKey, ShopNameKey, CursorKey };
        return await db.AppSettings.AsNoTracking().Where(x => keys.Contains(x.Key))
            .ToDictionaryAsync(x => x.Key, x => x.Value, cancellationToken);
    }

    private static async Task SetAsync(BoutiqueDbContext db, string key, string value, CancellationToken cancellationToken)
    {
        var setting = await db.AppSettings.SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (setting is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else { setting.Value = value; setting.UpdatedAt = DateTimeOffset.UtcNow; }
    }

    // --- Appairage ---------------------------------------------------------

    public async Task<SyncState> EnrollAsync(string serverUrl, string code, string deviceName, CancellationToken cancellationToken = default)
    {
        var baseUrl = Normalize(serverUrl);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // Ici on laisse remonter l'erreur : l'appairage est une action explicite du gérant, qui
        // doit savoir pourquoi elle échoue. Le reste de l'agent, lui, est silencieux.
        var response = await http.PostAsJsonAsync($"{baseUrl}/api/devices/enroll", new EnrollRequest(code.Trim().ToUpperInvariant(), deviceName), Json, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Appairage refusé par le serveur ({(int)response.StatusCode}). Vérifiez le code et l'adresse.");

        var result = await response.Content.ReadFromJsonAsync<EnrollResponse>(Json, cancellationToken)
            ?? throw new InvalidOperationException("Réponse d'appairage illisible.");

        await SetAsync(db, ServerUrlKey, baseUrl, cancellationToken);
        await SetAsync(db, TokenKey, result.DeviceToken, cancellationToken);
        await SetAsync(db, ShopIdKey, result.ShopId.ToString(), cancellationToken);
        await SetAsync(db, ShopNameKey, result.ShopName, cancellationToken);
        // Curseur à zéro : le référentiel entier redescendra au premier cycle.
        await SetAsync(db, CursorKey, "0", cancellationToken);
        db.AuditEntries.Add(new AuditEntry { Actor = "Responsable", Action = "Appairer le terminal", EntityType = "Sync", EntityId = result.ShopId.ToString(), AfterJson = JsonSerializer.Serialize(new { result.ShopName, deviceName }) });
        await db.SaveChangesAsync(cancellationToken);

        lastError = null;
        return await BuildStateAsync(db, cancellationToken);
    }

    public async Task<SyncState> ForgetAsync(string managerPin, CancellationToken cancellationToken = default)
    {
        if (!await authorization.AuthorizeSensitiveActionAsync(managerPin, "Désappairer le terminal", cancellationToken: cancellationToken))
            throw new UnauthorizedAccessException("Code gérant requis pour désappairer ce terminal.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var keys = new[] { TokenKey, ShopIdKey, ShopNameKey, CursorKey };
        var settings = await db.AppSettings.Where(x => keys.Contains(x.Key)).ToListAsync(cancellationToken);
        db.AppSettings.RemoveRange(settings);
        db.AuditEntries.Add(new AuditEntry { Actor = "Responsable", Action = "Désappairer le terminal", EntityType = "Sync", EntityId = "-" });
        await db.SaveChangesAsync(cancellationToken);

        lastError = null;
        return await BuildStateAsync(db, cancellationToken);
    }

    // --- Cycle -------------------------------------------------------------

    public async Task<SyncState> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        // Un cycle à la fois : le minuteur peut se déclencher pendant qu'un cycle traîne sur un
        // réseau lent, et deux cycles concurrents renverraient les mêmes événements.
        if (!await gate.WaitAsync(0, cancellationToken)) return await GetStateAsync(cancellationToken);

        isRunning = true;
        try
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var settings = await ReadSettingsAsync(db, cancellationToken);
            if (!settings.TryGetValue(TokenKey, out var token) || string.IsNullOrEmpty(token)
                || !settings.TryGetValue(ServerUrlKey, out var baseUrl) || string.IsNullOrEmpty(baseUrl))
                return await BuildStateAsync(db, cancellationToken);

            try
            {
                await PushAsync(db, baseUrl, token, cancellationToken);
                await PullAsync(db, baseUrl, token, settings.GetValueOrDefault(CursorKey), cancellationToken);
                await ReportStatusAsync(db, baseUrl, token, cancellationToken);
                lastSuccessAt = DateTimeOffset.Now;
                lastError = null;
            }
            catch (Exception e)
            {
                // Hors ligne est le cas nominal, pas une anomalie : on retient le message pour
                // l'afficher, et la caisse n'en sait rien.
                lastError = e.Message;
            }

            return await BuildStateAsync(db, cancellationToken);
        }
        finally
        {
            isRunning = false;
            gate.Release();
        }
    }

    /// <summary>
    /// Déclare la version en service, celle qui attend, et le dernier échec de mise à jour.
    ///
    /// Le terminal ne calcule rien ici : il relit ce que <c>UpdateAgent</c> a écrit dans les
    /// réglages. La mécanique Velopack est une affaire de l'application WPF, la synchronisation
    /// n'en est que le facteur — c'est ce qui permet à cette couche de rester compilable et
    /// testable hors Windows.
    /// </summary>
    private async Task ReportStatusAsync(BoutiqueDbContext db, string baseUrl, string token, CancellationToken cancellationToken)
    {
        var keys = new[] { UpdateService.CurrentVersionKey, UpdateService.PendingVersionKey, UpdateService.LastErrorKey };
        var rows = await db.AppSettings.AsNoTracking().Where(x => keys.Contains(x.Key))
            .ToDictionaryAsync(x => x.Key, x => x.Value, cancellationToken);

        var payload = new DeviceStatusRequest(
            Empty(rows.GetValueOrDefault(UpdateService.CurrentVersionKey)),
            Empty(rows.GetValueOrDefault(UpdateService.PendingVersionKey)),
            Empty(rows.GetValueOrDefault(UpdateService.LastErrorKey)));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/devices/status")
        {
            Content = JsonContent.Create(payload, options: Json),
        };
        request.Headers.Add("Authorization", $"Bearer {token}");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private async Task PushAsync(BoutiqueDbContext db, string baseUrl, string token, CancellationToken cancellationToken)
    {
        while (true)
        {
            var batch = await db.SyncOutbox
                .Where(x => x.SentAt == null)
                .OrderBy(x => x.CreatedAt)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0) return;

            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/sync/push")
            {
                Content = JsonContent.Create(new SyncPushRequest([.. batch.Select(Outbox.ToEvent)]), options: Json),
            };
            request.Headers.Authorization = new("Bearer", token);

            var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<SyncPushResponse>(Json, cancellationToken)
                ?? throw new InvalidOperationException("Réponse de remontée illisible.");

            var now = DateTimeOffset.UtcNow;
            var accepted = result.AcceptedIds.ToHashSet();
            foreach (var entry in batch)
            {
                entry.AttemptCount++;
                if (accepted.Contains(entry.Id)) { entry.SentAt = now; entry.LastError = null; continue; }

                var rejection = result.Rejected.FirstOrDefault(x => x.Id == entry.Id);
                // Un refus est définitif : le renvoyer indéfiniment bloquerait tout ce qui suit.
                // On le marque envoyé en conservant le motif, visible dans l'audit.
                if (rejection is not null) { entry.SentAt = now; entry.LastError = rejection.Reason; }
            }
            await db.SaveChangesAsync(cancellationToken);

            if (batch.Count < BatchSize) return;
        }
    }

    private async Task PullAsync(BoutiqueDbContext db, string baseUrl, string token, string? cursorValue, CancellationToken cancellationToken)
    {
        var cursor = long.TryParse(cursorValue, out var parsed) ? parsed : 0;

        while (true)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/sync/pull?since={cursor}");
            request.Headers.Authorization = new("Bearer", token);

            var response = await http.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<SyncPullResponse>(Json, cancellationToken)
                ?? throw new InvalidOperationException("Réponse de descente illisible.");

            await ApplyCatalogAsync(db, page, cancellationToken);
            cursor = page.Cursor;
            await SetAsync(db, CursorKey, cursor.ToString(), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            if (!page.HasMore) return;
        }
    }

    /// <summary>
    /// Applique le référentiel descendu. Les quantités ne sont jamais touchées : le stock
    /// appartient au terminal, et l'écraser avec une valeur venue du serveur ferait disparaître
    /// les ventes encaissées hors ligne pas encore remontées.
    /// </summary>
    private static async Task ApplyCatalogAsync(BoutiqueDbContext db, SyncPullResponse page, CancellationToken cancellationToken)
    {
        foreach (var dto in page.Categories)
        {
            var row = await db.Categories.SingleOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (row is null) { row = new Category { Id = dto.Id }; db.Categories.Add(row); }
            row.Name = dto.Name; row.IsActive = dto.IsActive; row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        foreach (var dto in page.Products)
        {
            var row = await db.Products.SingleOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (row is null) { row = new Product { Id = dto.Id }; db.Products.Add(row); }
            row.CategoryId = dto.CategoryId; row.Name = dto.Name; row.Brand = dto.Brand;
            row.Description = dto.Description; row.SubCategory = dto.SubCategory; row.Gender = dto.Gender;
            row.Season = dto.Season; row.Type = dto.Type; row.IsActive = dto.IsActive;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        foreach (var dto in page.Variants)
        {
            var row = await db.ProductVariants.SingleOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (row is null)
            {
                // Un article qui arrive du serveur n'a pas encore de stock ici : il en prendra
                // à la première réception, pas à la synchronisation.
                row = new ProductVariant { Id = dto.Id, QuantityOnHand = 0, QuantityReserved = 0 };
                db.ProductVariants.Add(row);
            }
            row.ProductId = dto.ProductId; row.Sku = dto.Sku; row.Barcode = dto.Barcode;
            row.Size = dto.Size; row.Color = dto.Color; row.Material = dto.Material; row.Supplier = dto.Supplier;
            row.CostXof = dto.CostXof; row.PriceXof = dto.PriceXof;
            row.PromotionalPriceXof = dto.PromotionalPriceXof;
            row.PromotionStartsAt = dto.PromotionStartsAt; row.PromotionEndsAt = dto.PromotionEndsAt;
            row.LowStockThreshold = dto.LowStockThreshold; row.IsActive = dto.IsActive;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        // Articles dont la portée s'est resserrée sur une autre boutique. Ils ne sont pas
        // supprimés — leur historique de ventes y renvoie — mais désactivés, donc invendables.
        foreach (var id in page.RetiredProductIds ?? [])
        {
            var product = await db.Products.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (product is null) continue;
            product.IsActive = false;
            product.UpdatedAt = DateTimeOffset.UtcNow;
            foreach (var variant in await db.ProductVariants.Where(x => x.ProductId == id).ToListAsync(cancellationToken))
                variant.IsActive = false;
        }

        // Les commandes descendent avec leur état courant. Elles sont remplacées et non
        // fusionnées : le serveur en est la source, et une annulation décidée depuis le téléphone
        // doit effacer ce que la caisse croyait savoir.
        foreach (var dto in page.Orders ?? [])
        {
            var order = await db.Orders.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (order is null)
            {
                order = new Order { Id = dto.Id, PlacedAt = dto.PlacedAt };
                db.Orders.Add(order);
            }
            else
            {
                db.OrderLines.RemoveRange(order.Lines);
                order.Lines.Clear();
            }
            order.Number = dto.Number; order.CustomerName = dto.CustomerName; order.Phone = dto.Phone;
            order.Note = dto.Note; order.Channel = dto.Channel; order.Status = dto.Status;
            order.TotalXof = dto.TotalXof; order.SaleId = dto.SaleId; order.UpdatedAt = DateTimeOffset.UtcNow;
            foreach (var line in dto.Lines)
                order.Lines.Add(new OrderLine
                {
                    OrderId = order.Id, VariantId = line.VariantId, Sku = line.Sku,
                    Description = line.Description, Quantity = line.Quantity, UnitPriceXof = line.UnitPriceXof,
                });
        }

        foreach (var dto in page.Settings)
        {
            // Les clés de synchronisation appartiennent au terminal : les laisser être écrasées
            // par le serveur reviendrait à lui laisser effacer son propre jeton.
            if (dto.Key.StartsWith("Sync.", StringComparison.Ordinal) || dto.Key.StartsWith("Security.", StringComparison.Ordinal)) continue;
            await SetAsync(db, dto.Key, dto.Value, cancellationToken);
        }
    }

    private static string Normalize(string url)
    {
        var trimmed = (url ?? string.Empty).Trim().TrimEnd('/');
        if (trimmed.Length == 0) throw new ArgumentException("L'adresse du serveur est obligatoire.", nameof(url));
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            trimmed = "https://" + trimmed;
        return trimmed;
    }
}
