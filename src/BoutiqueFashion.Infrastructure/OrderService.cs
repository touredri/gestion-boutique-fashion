using BoutiqueFashion.Application;
using BoutiqueFashion.Contracts;
using BoutiqueFashion.Domain;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Infrastructure;

/// <summary>
/// Commandes reçues du site vitrine, telles que la caisse les voit.
///
/// La caisse ne crée pas de commande et n'en annule pas : elles naissent en ligne et s'annulent
/// depuis le téléphone. Elle n'en change l'état qu'en agissant réellement — encaisser, puis
/// remettre la marchandise.
/// </summary>
public sealed class OrderService(IDbContextFactory<BoutiqueDbContext> factory) : IOrderService
{
    public async Task<IReadOnlyList<OrderRow>> ListAsync(bool includeClosed = false, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.Orders.AsNoTracking().Include(x => x.Lines).AsQueryable();
        if (!includeClosed) query = query.Where(x => x.Status == OrderStatus.Pending || x.Status == OrderStatus.Processed);

        var rows = await query.OrderByDescending(x => x.PlacedAt).Take(200).ToListAsync(cancellationToken);
        return [.. rows.Select(x => new OrderRow(
            x.Id, x.Number, x.CustomerName, x.Phone, x.Note, x.Channel, x.Status, x.TotalXof, x.SaleId, x.PlacedAt,
            [.. x.Lines.Select(l => new OrderLineRow(l.VariantId, l.Sku, l.Description, l.Quantity, l.UnitPriceXof))]))];
    }

    public async Task MarkDeliveredAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == orderId, cancellationToken)
            ?? throw new KeyNotFoundException("Commande introuvable.");

        // On ne remet pas une marchandise qui n'a pas été payée : la vente doit exister d'abord.
        if (order.Status != OrderStatus.Processed)
            throw new InvalidOperationException("Encaissez d'abord la commande : la livraison suit la vente, jamais l'inverse.");

        order.Status = OrderStatus.Delivered;
        order.UpdatedAt = DateTimeOffset.UtcNow;
        Outbox.Enqueue(db, SyncEntityTypes.OrderStatus, order.Id,
            new OrderStatusPayload(order.Id, OrderStatus.Delivered, order.SaleId, DateTimeOffset.UtcNow));
        db.AuditEntries.Add(new AuditEntry { Actor = "Vendeur boutique", Action = "Livrer commande", EntityType = nameof(Order), EntityId = order.Id.ToString(), AfterJson = order.Number });
        await db.SaveChangesAsync(cancellationToken);
    }
}
