using System.Text.Json;
using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Infrastructure;

public sealed class PurchaseService(IDbContextFactory<BoutiqueDbContext> factory) : IPurchaseService
{
    public async Task<Guid> CreateOrderAsync(string supplier, IReadOnlyList<PurchaseLineDraft> lines, string? note = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(supplier)) throw new ArgumentException("Le fournisseur est obligatoire.");
        if (lines.Count == 0) throw new InvalidOperationException("Ajoutez au moins une ligne attendue.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var ids = lines.Select(x => x.VariantId).ToArray();
        var existing = await db.ProductVariants.Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync(cancellationToken);
        foreach (var line in lines)
        {
            if (!existing.Contains(line.VariantId)) throw new KeyNotFoundException("Variante introuvable.");
            if (line.ExpectedQuantity <= 0) throw new InvalidOperationException("La quantité attendue doit être positive.");
        }
        var order = new PurchaseOrder { Supplier = supplier.Trim(), Note = note };
        db.PurchaseOrders.Add(order);
        foreach (var line in lines) db.PurchaseOrderLines.Add(new PurchaseOrderLine { Order = order, PurchaseOrderId = order.Id, VariantId = line.VariantId, ExpectedQuantity = line.ExpectedQuantity });
        db.AuditEntries.Add(new AuditEntry { Actor = "Responsable", Action = "Créer commande fournisseur", EntityType = nameof(PurchaseOrder), EntityId = order.Id.ToString(), AfterJson = JsonSerializer.Serialize(new { supplier, lignes = lines.Count }) });
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return order.Id;
    }

    public async Task<IReadOnlyList<PurchaseOrderRow>> ListOpenAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.PurchaseOrderLines.AsNoTracking()
            .Where(x => x.Order!.Status == PurchaseOrderStatus.Open)
            .OrderBy(x => x.Order!.Supplier).ThenBy(x => x.Variant!.Sku)
            .Select(x => new PurchaseOrderRow(x.PurchaseOrderId, x.Id, x.Order!.Supplier, x.Variant!.Sku, x.Variant!.Product!.Name, x.ExpectedQuantity, x.ReceivedQuantity))
            .ToListAsync(cancellationToken);
    }

    public async Task ReceiveAsync(Guid orderLineId, decimal receivedQuantity, long unitCostXof = 0, string actor = "Responsable", CancellationToken cancellationToken = default)
    {
        if (receivedQuantity <= 0) throw new InvalidOperationException("La quantité reçue doit être positive.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var line = await db.PurchaseOrderLines.Include(x => x.Order).SingleOrDefaultAsync(x => x.Id == orderLineId, cancellationToken) ?? throw new KeyNotFoundException("Ligne de commande introuvable.");
        if (line.Order!.Status != PurchaseOrderStatus.Open) throw new InvalidOperationException("Commande déjà soldée.");
        var variant = await db.ProductVariants.SingleAsync(x => x.Id == line.VariantId, cancellationToken);
        var cost = unitCostXof > 0 ? unitCostXof : variant.CostXof;
        variant.WeightedAverageCostXof = BusinessRules.NewWeightedAverageCost(variant.QuantityOnHand, variant.WeightedAverageCostXof, receivedQuantity, cost);
        variant.QuantityOnHand += receivedQuantity;
        variant.CostXof = decimal.ToInt64(decimal.Round(variant.WeightedAverageCostXof, 0));
        variant.UpdatedAt = DateTimeOffset.UtcNow;
        line.ReceivedQuantity += receivedQuantity;
        db.StockMovements.Add(new StockMovement { VariantId = variant.Id, Type = StockMovementType.Receipt, QuantityDelta = receivedQuantity, UnitCostXof = cost, Reason = $"Réception {line.Order.Supplier} · attendu {line.ExpectedQuantity:0.###} reçu {line.ReceivedQuantity:0.###}", SourceType = "PurchaseOrder", SourceId = line.Order.Id, Actor = actor });
        var orderLines = await db.PurchaseOrderLines.Where(x => x.PurchaseOrderId == line.PurchaseOrderId).ToListAsync(cancellationToken);
        var allReceived = orderLines.All(x => x.ReceivedQuantity >= x.ExpectedQuantity);
        if (allReceived) line.Order.Status = PurchaseOrderStatus.Closed;
        db.AuditEntries.Add(new AuditEntry { Actor = actor, Action = "Réception fournisseur", EntityType = nameof(PurchaseOrderLine), EntityId = line.Id.ToString(), AfterJson = JsonSerializer.Serialize(new { received = receivedQuantity, totalReceived = line.ReceivedQuantity, expected = line.ExpectedQuantity, ecart = line.ReceivedQuantity - line.ExpectedQuantity }) });
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }
}
