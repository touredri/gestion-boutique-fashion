using System.Text.Json;
using BoutiqueFashion.Contracts;
using BoutiqueFashion.Domain;
using BoutiqueFashion.Server.Data;
using BoutiqueFashion.Server.Notifications;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Server.Sync;

/// <summary>
/// Applique les événements poussés par un terminal.
///
/// Deux garanties tiennent tout l'édifice :
///
/// 1. <b>Idempotence.</b> Chaque événement porte l'identifiant de sa ligne d'outbox, consigné à
///    l'application. Rejouer un lot est donc sans effet, ce qui permet au terminal de réessayer
///    sans jamais avoir à se demander si le lot précédent est passé.
/// 2. <b>Isolation des échecs.</b> Chaque événement est validé séparément. Un enregistrement
///    corrompu est refusé nominativement et la file continue : sans cela, une seule ligne
///    illisible gèlerait à jamais la remontée d'une boutique.
///
/// Le coût est un aller-retour par événement plutôt qu'un par lot. Pour une boutique qui produit
/// quelques centaines d'événements par jour, c'est sans conséquence.
/// </summary>
internal sealed class SyncApplier(ServerDbContext db, Notifier notifier)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Alertes retenues pendant l'application, envoyées une fois le lot écrit. Les
    /// émettre au fil de l'eau enverrait des messages pour des faits qu'une erreur ultérieure
    /// aurait annulés.</summary>
    private readonly List<Alert> pending = [];

    public async Task<SyncPushResponse> ApplyAsync(Guid shopId, IReadOnlyList<SyncEvent> events, CancellationToken cancellationToken)
    {
        var accepted = new List<Guid>();
        var rejected = new List<SyncRejection>();

        foreach (var e in events)
        {
            if (await db.ProcessedEvents.AnyAsync(x => x.Id == e.Id, cancellationToken))
            {
                // Déjà appliqué : on l'acquitte pour que le terminal cesse de le renvoyer.
                accepted.Add(e.Id);
                continue;
            }

            try
            {
                await ApplyOneAsync(shopId, e, cancellationToken);
                db.ProcessedEvents.Add(new ProcessedEvent { Id = e.Id, ShopId = shopId, EntityType = e.EntityType });
                await db.SaveChangesAsync(cancellationToken);
                accepted.Add(e.Id);
            }
            catch (Exception ex)
            {
                // Sans ce nettoyage, les entités en échec resteraient suivies et feraient échouer
                // tous les événements suivants du lot.
                db.ChangeTracker.Clear();
                rejected.Add(new SyncRejection(e.Id, ex.Message));
            }
        }

        foreach (var alert in pending) await notifier.SendAsync(alert, cancellationToken);
        pending.Clear();

        return new SyncPushResponse(accepted, rejected);
    }

    private Task ApplyOneAsync(Guid shopId, SyncEvent e, CancellationToken cancellationToken) => e.EntityType switch
    {
        SyncEntityTypes.Sale => ApplySaleAsync(shopId, Read<SalePayload>(e), cancellationToken),
        SyncEntityTypes.CashSessionOpened => ApplyCashOpenedAsync(shopId, Read<CashSessionOpenedPayload>(e), cancellationToken),
        SyncEntityTypes.CashSessionClosed => ApplyCashClosedAsync(shopId, Read<CashSessionClosedPayload>(e), cancellationToken),
        SyncEntityTypes.CashMovement => ApplyCashMovementAsync(shopId, Read<CashMovementPayload>(e), cancellationToken),
        SyncEntityTypes.Expense => ApplyExpenseAsync(shopId, Read<ExpensePayload>(e), cancellationToken),
        SyncEntityTypes.CreditPayment => ApplyCreditPaymentAsync(shopId, Read<CreditPaymentPayload>(e), cancellationToken),
        SyncEntityTypes.Customer => ApplyCustomerAsync(Read<CustomerPayload>(e), cancellationToken),
        SyncEntityTypes.StockMovement => ApplyStockMovementAsync(shopId, Read<StockMovementPayload>(e), cancellationToken),
        _ => throw new InvalidOperationException($"Type d'événement inconnu : {e.EntityType}."),
    };

    private static T Read<T>(SyncEvent e) =>
        JsonSerializer.Deserialize<T>(e.PayloadJson, Json) ?? throw new InvalidDataException($"Charge utile illisible pour {e.EntityType}.");

    // --- Ventes ------------------------------------------------------------

    private async Task ApplySaleAsync(Guid shopId, SalePayload p, CancellationToken cancellationToken)
    {
        var existing = await db.Sales.Include(x => x.Lines).Include(x => x.Payments).SingleOrDefaultAsync(x => x.Id == p.Id, cancellationToken);
        if (existing is null)
        {
            db.Sales.Add(new Data.Sale
            {
                Id = p.Id,
                ShopId = shopId,
                Number = p.Number,
                IdempotencyKey = p.IdempotencyKey,
                CustomerId = p.CustomerId,
                CashSessionId = p.CashSessionId,
                SellerName = p.SellerName,
                SubtotalXof = p.SubtotalXof,
                DiscountXof = p.DiscountXof,
                TotalXof = p.TotalXof,
                ChangeXof = p.ChangeXof,
                Status = p.Status,
                OccurredAt = p.CreatedAt,
                Lines = [.. p.Lines.Select(l => new Data.SaleLine { SaleId = p.Id, VariantId = l.VariantId, Sku = l.Sku, Description = l.Description, Quantity = l.Quantity, UnitPriceXof = l.UnitPriceXof, FrozenUnitCostXof = l.FrozenUnitCostXof, DiscountXof = l.DiscountXof, LineTotalXof = l.LineTotalXof })],
                Payments = [.. p.Payments.Select(x => new Data.SalePayment { Id = x.Id, SaleId = p.Id, Mode = x.Mode, AmountXof = x.AmountXof, ExternalReference = x.ExternalReference, IsReversal = x.IsReversal })],
            });
        }
        else
        {
            // Une vente peut revenir : avance soldée, annulation. Seul l'état évolue, jamais son
            // contenu — les lignes d'une vente passée ne se réécrivent pas.
            existing.Status = p.Status;
            existing.TotalXof = p.TotalXof;
        }

        if (p.Credit is { } credit)
        {
            var stored = await db.Credits.SingleOrDefaultAsync(x => x.Id == credit.Id, cancellationToken);
            if (stored is null)
                db.Credits.Add(new Credit { Id = credit.Id, ShopId = shopId, SaleId = p.Id, CustomerId = credit.CustomerId, OriginalAmountXof = credit.OriginalAmountXof, BalanceXof = credit.BalanceXof, DueAt = credit.DueAt, Status = credit.Status });
            else { stored.BalanceXof = credit.BalanceXof; stored.Status = credit.Status; }
        }
    }

    // --- Caisse ------------------------------------------------------------

    private async Task ApplyCashOpenedAsync(Guid shopId, CashSessionOpenedPayload p, CancellationToken cancellationToken)
    {
        if (await db.CashSessions.AnyAsync(x => x.Id == p.Id, cancellationToken)) return;
        db.CashSessions.Add(new Data.CashSession
        {
            Id = p.Id, ShopId = shopId, Number = p.Number, OperatorName = p.OperatorName,
            OpeningFloatXof = p.OpeningFloatXof, OpenedAt = p.OpenedAt, IsClosed = false,
        });

        var shop = await db.Shops.AsNoTracking().SingleOrDefaultAsync(x => x.Id == shopId, cancellationToken);
        pending.Add(new Alert(NotificationKind.CashOpened, $"Caisse ouverte · {shop?.Name}",
            $"{p.OperatorName} a ouvert la caisse avec {p.OpeningFloatXof:N0} F de fond."));
    }

    private async Task ApplyCashClosedAsync(Guid shopId, CashSessionClosedPayload p, CancellationToken cancellationToken)
    {
        var session = await db.CashSessions.SingleOrDefaultAsync(x => x.Id == p.Id, cancellationToken);
        if (session is null)
        {
            // La clôture peut arriver sans son ouverture si la file a été purgée : on reconstruit
            // plutôt que de refuser, car une caisse clôturée est une information trop précieuse.
            session = new Data.CashSession { Id = p.Id, ShopId = shopId, Number = p.Number, OpenedAt = p.OpenedAt };
            db.CashSessions.Add(session);
        }
        session.OperatorName = p.OperatorName;
        session.ClosedBy = p.ClosedBy;
        session.OpeningFloatXof = p.OpeningFloatXof;
        session.ExpectedCashXof = p.ExpectedCashXof;
        session.CountedCashXof = p.CountedCashXof;
        session.DifferenceXof = p.DifferenceXof;
        session.DifferenceReason = p.DifferenceReason;
        session.ClosedAt = p.ClosedAt;
        session.IsClosed = true;

        var shop = await db.Shops.AsNoTracking().SingleOrDefaultAsync(x => x.Id == shopId, cancellationToken);
        // Un écart mérite sa propre alerte : il se règle le soir même, pas au prochain passage.
        pending.Add(p.DifferenceXof == 0
            ? new Alert(NotificationKind.CashClosed, $"Caisse clôturée · {shop?.Name}",
                $"{p.OperatorName} a clôturé sans écart. {p.CountedCashXof:N0} F comptés.")
            : new Alert(NotificationKind.CashVariance, $"Écart de caisse · {shop?.Name}",
                $"{p.OperatorName} a clôturé avec {p.DifferenceXof:N0} F d'écart. "
                + $"Attendu {p.ExpectedCashXof:N0} F, compté {p.CountedCashXof:N0} F."
                + (string.IsNullOrWhiteSpace(p.DifferenceReason) ? "" : $" Motif : {p.DifferenceReason}.")));
    }

    private async Task ApplyCashMovementAsync(Guid shopId, CashMovementPayload p, CancellationToken cancellationToken)
    {
        if (await db.CashMovements.AnyAsync(x => x.Id == p.Id, cancellationToken)) return;
        db.CashMovements.Add(new Data.CashMovement
        {
            Id = p.Id, ShopId = shopId, CashSessionId = p.CashSessionId, Direction = p.Direction,
            AmountXof = p.AmountXof, Reason = p.Reason, Actor = p.Actor, OccurredAt = p.CreatedAt,
        });
    }

    private async Task ApplyExpenseAsync(Guid shopId, ExpensePayload p, CancellationToken cancellationToken)
    {
        if (await db.Expenses.AnyAsync(x => x.Id == p.Id, cancellationToken)) return;
        db.Expenses.Add(new Data.Expense
        {
            Id = p.Id, ShopId = shopId, Category = p.Category, Description = p.Description,
            AmountXof = p.AmountXof, Mode = p.Mode, OccurredAt = p.CreatedAt,
        });
    }

    private async Task ApplyCreditPaymentAsync(Guid shopId, CreditPaymentPayload p, CancellationToken cancellationToken)
    {
        if (await db.CreditPayments.AnyAsync(x => x.Id == p.Id, cancellationToken)) return;
        db.CreditPayments.Add(new Data.CreditPayment
        {
            Id = p.Id, ShopId = shopId, CreditId = p.CustomerCreditId, Number = p.Number,
            AmountXof = p.AmountXof, Mode = p.Mode, IsReversal = p.IsReversal,
            ReversesPaymentId = p.ReversesPaymentId, Actor = p.Actor, OccurredAt = p.CreatedAt,
        });

        // Le solde est recalculé à partir des versements reçus plutôt que décrémenté : si un
        // versement arrive deux fois ou dans le désordre, le total reste juste.
        var credit = await db.Credits.SingleOrDefaultAsync(x => x.Id == p.CustomerCreditId, cancellationToken);
        if (credit is not null)
        {
            var paid = await db.CreditPayments.Where(x => x.CreditId == credit.Id).SumAsync(x => x.AmountXof, cancellationToken) + p.AmountXof;
            credit.BalanceXof = credit.OriginalAmountXof - paid;
            credit.Status = credit.BalanceXof <= 0 ? CreditStatus.Paid : paid > 0 ? CreditStatus.PartiallyPaid : CreditStatus.Due;
        }
    }

    // --- Clients -----------------------------------------------------------

    private async Task ApplyCustomerAsync(CustomerPayload p, CancellationToken cancellationToken)
    {
        var existing = await db.Customers.SingleOrDefaultAsync(x => x.Id == p.Id, cancellationToken);
        var stamp = p.UpdatedAt ?? p.CreatedAt;

        if (existing is null)
        {
            db.Customers.Add(new Data.Customer
            {
                Id = p.Id, Name = p.Name, Phone = p.Phone, SecondaryPhone = p.SecondaryPhone,
                Gender = p.Gender, Address = p.Address, Notes = p.Notes, Preferences = p.Preferences,
                PreferredChannel = p.PreferredChannel, CreditLimitXof = p.CreditLimitXof,
                MarketingConsent = p.MarketingConsent, IsArchived = p.IsArchived,
                CreatedAt = p.CreatedAt, UpdatedAt = stamp,
            });
            return;
        }

        // Seule donnée modifiable des deux côtés : dernier écrit gagnant. Une version plus
        // ancienne que celle en base est ignorée, sinon un terminal longtemps hors ligne
        // écraserait au retour des corrections faites entre-temps.
        if (stamp < existing.UpdatedAt) return;

        existing.Name = p.Name; existing.Phone = p.Phone; existing.SecondaryPhone = p.SecondaryPhone;
        existing.Gender = p.Gender; existing.Address = p.Address; existing.Notes = p.Notes;
        existing.Preferences = p.Preferences; existing.PreferredChannel = p.PreferredChannel;
        existing.CreditLimitXof = p.CreditLimitXof; existing.MarketingConsent = p.MarketingConsent;
        existing.IsArchived = p.IsArchived; existing.UpdatedAt = stamp;
    }

    // --- Stock -------------------------------------------------------------

    private async Task ApplyStockMovementAsync(Guid shopId, StockMovementPayload p, CancellationToken cancellationToken)
    {
        if (await db.StockMovements.AnyAsync(x => x.Id == p.Id, cancellationToken)) return;
        db.StockMovements.Add(new Data.StockMovement
        {
            Id = p.Id, ShopId = shopId, VariantId = p.VariantId, Type = p.Type,
            QuantityDelta = p.QuantityDelta, UnitCostXof = p.UnitCostXof, Reason = p.Reason,
            SourceType = p.SourceType, SourceId = p.SourceId, Actor = p.Actor, OccurredAt = p.CreatedAt,
        });

        var stock = await db.ShopStocks.SingleOrDefaultAsync(x => x.ShopId == shopId && x.VariantId == p.VariantId, cancellationToken);
        if (stock is null)
        {
            stock = new ShopStock { ShopId = shopId, VariantId = p.VariantId };
            db.ShopStocks.Add(stock);
        }

        // Mise de côté et levée ne touchent pas le stock physique, seulement la part réservée.
        // Les deux s'expriment pareil : le delta est négatif à la réservation, positif à la levée.
        if (p.Type is StockMovementType.Reservation or StockMovementType.ReservationRelease)
            stock.QuantityReserved -= p.QuantityDelta;
        else
            stock.QuantityOnHand += p.QuantityDelta;

        if (p.Type == StockMovementType.Receipt && p.UnitCostXof > 0) stock.LastUnitCostXof = p.UnitCostXof;
        stock.UpdatedAt = DateTimeOffset.UtcNow;
    }
}
