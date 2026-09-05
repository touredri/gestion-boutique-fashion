using BoutiqueFashion.Domain;
using BoutiqueFashion.Server.Data;
using BoutiqueFashion.Server.Notifications;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Server.Endpoints;

public sealed record OrderLineInput(Guid VariantId, decimal Quantity);
public sealed record OrderInput(Guid ShopId, string CustomerName, string Phone, string? Note, IReadOnlyList<OrderLineInput> Lines);

public sealed record OrderLineView(Guid VariantId, string Sku, string Description, decimal Quantity, long UnitPriceXof);
public sealed record OrderView(
    Guid Id, Guid ShopId, string ShopName, string Number, string CustomerName, string Phone, string? Note,
    OrderChannel Channel, OrderStatus Status, long TotalXof, Guid? SaleId,
    DateTimeOffset CreatedAt, DateTimeOffset? ProcessedAt, DateTimeOffset? DeliveredAt, string? CancelReason,
    IReadOnlyList<OrderLineView> Lines);

/// <summary>Article tel que le site vitrine le montre : sans coût d'achat, sans quantité exacte.
/// Un visiteur n'a pas à connaître la marge, et afficher « il en reste 2 » invite à négocier.</summary>
public sealed record ShowcaseItem(
    Guid VariantId, Guid ProductId, string Name, string? Brand, string? Description,
    string Category, string? Gender, ProductType Type,
    string? Size, string? Color, long PriceXof, long? PromotionalPriceXof, bool InStock,
    IReadOnlyList<Guid> ShopIds);

public sealed record ShowcaseShop(Guid Id, string Name, string? City, string? Address, string? Phone);
public sealed record Showcase(IReadOnlyList<ShowcaseShop> Shops, IReadOnlyList<ShowcaseItem> Items);

internal static class Orders
{
    /// <summary>Routes publiques du site vitrine. Anonymes par nature : un visiteur ne se
    /// connecte pas pour regarder une robe.</summary>
    public static void MapShowcase(this WebApplication app)
    {
        app.MapGet("/api/public/showcase", async (ServerDbContext db, CancellationToken ct) =>
        {
            var shops = await db.Shops.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name)
                .Select(x => new ShowcaseShop(x.Id, x.Name, x.City, x.Address, x.Phone)).ToListAsync(ct);

            var rows = await db.Variants.AsNoTracking().Where(x => x.IsActive)
                .Join(db.Products.Where(p => p.IsActive), v => v.ProductId, p => p.Id, (v, p) => new { v, p })
                .Join(db.Categories, x => x.p.CategoryId, c => c.Id, (x, c) => new { x.v, x.p, Category = c })
                .ToListAsync(ct);

            // Le stock par boutique sert uniquement à dire « disponible » ou non. La quantité
            // exacte reste à l'intérieur : elle n'aide pas le visiteur et invite à négocier.
            var stock = await db.ShopStocks.AsNoTracking()
                .Where(x => x.QuantityOnHand - x.QuantityReserved > 0)
                .Select(x => new { x.ShopId, x.VariantId })
                .ToListAsync(ct);
            var byVariant = stock.GroupBy(x => x.VariantId).ToDictionary(g => g.Key, g => g.Select(x => x.ShopId).ToList());

            var now = DateTimeOffset.UtcNow;
            var items = rows.Select(x => new ShowcaseItem(
                x.v.Id, x.p.Id, x.p.Name, x.p.Brand, x.p.Description, x.Category.Name, x.p.Gender, x.p.Type,
                x.v.Size, x.v.Color, x.v.PriceXof,
                // La promotion n'est annoncée que si elle court réellement : une remise expirée
                // affichée en vitrine est une promesse qu'on ne tiendra pas au comptoir.
                x.v.PromotionalPriceXof is not null && x.v.PromotionStartsAt <= now && x.v.PromotionEndsAt >= now
                    ? x.v.PromotionalPriceXof : null,
                byVariant.ContainsKey(x.v.Id),
                // Un article exclusif n'est proposé que pour sa boutique.
                x.p.ShopId is { } only ? [only] : byVariant.GetValueOrDefault(x.v.Id, [])))
                .Where(x => x.ShopIds.Count > 0 || x.InStock)
                .OrderBy(x => x.Name)
                .ToList();

            return Results.Ok(new Showcase(shops, items));
        });

        app.MapPost("/api/public/orders", async (OrderInput input, ServerDbContext db, Notifier notifier, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(input.CustomerName) || string.IsNullOrWhiteSpace(input.Phone))
                return Results.BadRequest(new { error = "Votre nom et votre téléphone sont nécessaires pour vous rappeler." });
            if (input.Lines.Count == 0)
                return Results.BadRequest(new { error = "Votre panier est vide." });
            if (!await db.Shops.AnyAsync(x => x.Id == input.ShopId && x.IsActive, ct))
                return Results.BadRequest(new { error = "Boutique inconnue." });

            var ids = input.Lines.Select(x => x.VariantId).Distinct().ToList();
            var variants = await db.Variants.AsNoTracking().Where(x => ids.Contains(x.Id) && x.IsActive)
                .Join(db.Products, v => v.ProductId, p => p.Id, (v, p) => new { v, ProductName = p.Name })
                .ToDictionaryAsync(x => x.v.Id, ct);
            if (variants.Count != ids.Count) return Results.BadRequest(new { error = "Un article demandé n'est plus disponible." });

            var now = DateTimeOffset.UtcNow;
            var order = new Order
            {
                ShopId = input.ShopId,
                Number = await NextNumberAsync(db, ct),
                CustomerName = input.CustomerName.Trim(),
                Phone = new string([.. input.Phone.Where(c => char.IsDigit(c) || c == '+')]),
                Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim(),
                Seq = await db.NextSeqAsync(ct),
            };

            foreach (var line in input.Lines)
            {
                if (line.Quantity <= 0) return Results.BadRequest(new { error = "Quantité invalide." });
                var item = variants[line.VariantId];
                var price = item.v.PromotionalPriceXof is not null && item.v.PromotionStartsAt <= now && item.v.PromotionEndsAt >= now
                    ? item.v.PromotionalPriceXof.Value
                    : item.v.PriceXof;
                order.Lines.Add(new OrderLine
                {
                    OrderId = order.Id,
                    VariantId = item.v.Id,
                    Sku = item.v.Sku,
                    Description = string.Join(" · ", new[] { item.ProductName, item.v.Color, item.v.Size }.Where(x => !string.IsNullOrWhiteSpace(x))),
                    Quantity = line.Quantity,
                    UnitPriceXof = price,
                });
                order.TotalXof += (long)(price * line.Quantity);
            }

            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);

            var shop = await db.Shops.AsNoTracking().SingleAsync(x => x.Id == order.ShopId, ct);
            await notifier.SendAsync(new Alert(NotificationKind.NewOrder, $"Nouvelle commande · {shop.Name}",
                $"{order.CustomerName} ({order.Phone}) — {order.Lines.Count} article(s), {order.TotalXof:N0} F. Réf. {order.Number}."), ct);

            // La cliente reçoit sa référence, pas l'identifiant interne : c'est ce qu'elle
            // donnera au téléphone.
            return Results.Ok(new { order.Number, order.TotalXof, ShopName = shop.Name });
        });
    }

    /// <summary>Gestion des commandes depuis l'application de pilotage.</summary>
    public static RouteGroupBuilder MapOrders(this RouteGroupBuilder group)
    {
        group.MapGet("/orders", async (ServerDbContext db, Guid? shopId, bool includeClosed, CancellationToken ct) =>
        {
            var orders = db.Orders.AsNoTracking().AsQueryable();
            if (shopId is { } only) orders = orders.Where(x => x.ShopId == only);
            if (!includeClosed) orders = orders.Where(x => x.Status == OrderStatus.Pending || x.Status == OrderStatus.Processed);

            var rows = await orders
                .Join(db.Shops, o => o.ShopId, s => s.Id, (o, s) => new { Order = o, Shop = s })
                .OrderByDescending(x => x.Order.CreatedAt)
                .Take(200)
                .Select(x => new OrderView(
                    x.Order.Id, x.Order.ShopId, x.Shop.Name, x.Order.Number, x.Order.CustomerName, x.Order.Phone, x.Order.Note,
                    x.Order.Channel, x.Order.Status, x.Order.TotalXof, x.Order.SaleId,
                    x.Order.CreatedAt, x.Order.ProcessedAt, x.Order.DeliveredAt, x.Order.CancelReason,
                    x.Order.Lines.Select(l => new OrderLineView(l.VariantId, l.Sku, l.Description, l.Quantity, l.UnitPriceXof)).ToList()))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        group.MapPost("/orders/{id:guid}/cancel", async (Guid id, CancelInput input, ServerDbContext db, CancellationToken ct) =>
        {
            var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (order is null) return Results.NotFound();
            if (order.Status == OrderStatus.Delivered) return Results.BadRequest(new { error = "Une commande livrée ne s'annule pas." });

            order.Status = OrderStatus.Cancelled;
            order.CancelReason = input.Reason;
            // Le curseur avance : la caisse doit apprendre l'annulation, sans quoi elle
            // continuerait de proposer d'encaisser une commande abandonnée.
            order.Seq = await db.NextSeqAsync(ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapPost("/orders/{id:guid}/reroute", async (Guid id, RerouteInput input, ServerDbContext db, CancellationToken ct) =>
        {
            var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (order is null) return Results.NotFound();
            if (order.Status != OrderStatus.Pending) return Results.BadRequest(new { error = "Seule une commande en cours peut changer de boutique." });
            if (!await db.Shops.AnyAsync(x => x.Id == input.ShopId && x.IsActive, ct)) return Results.BadRequest(new { error = "Boutique inconnue." });

            order.ShopId = input.ShopId;
            order.Seq = await db.NextSeqAsync(ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return group;
    }

    /// <summary>Numérotation annuelle et globale : une commande vient du site, pas d'une caisse,
    /// et sa référence doit être unique quand la cliente la donne au téléphone.</summary>
    private static async Task<string> NextNumberAsync(ServerDbContext db, CancellationToken ct)
    {
        var year = DateTimeOffset.UtcNow.Year;
        var prefix = $"CMD-{year}-";
        var last = await db.Orders.AsNoTracking().Where(x => x.Number.StartsWith(prefix))
            .OrderByDescending(x => x.Number).Select(x => x.Number).FirstOrDefaultAsync(ct);
        var next = last is null ? 1 : int.Parse(last[prefix.Length..]) + 1;
        return $"{prefix}{next:D4}";
    }
}

internal sealed record CancelInput(string? Reason);
internal sealed record RerouteInput(Guid ShopId);
