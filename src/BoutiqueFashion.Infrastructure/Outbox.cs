using System.Text.Json;
using BoutiqueFashion.Contracts;
using BoutiqueFashion.Domain;

namespace BoutiqueFashion.Infrastructure;

/// <summary>
/// Écriture dans la file de synchronisation.
///
/// Toujours appelée <b>avant</b> le SaveChanges de l'opération métier, jamais après : la ligne de
/// file et la donnée qu'elle décrit doivent tomber dans la même transaction. Publier après coup
/// laisserait une fenêtre où une vente est encaissée mais ne remontera jamais.
/// </summary>
internal static class Outbox
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static void Enqueue<T>(BoutiqueDbContext db, string entityType, Guid entityId, T payload) =>
        db.SyncOutbox.Add(new SyncOutboxEntry
        {
            EntityType = entityType,
            EntityId = entityId,
            PayloadJson = JsonSerializer.Serialize(payload, Options),
        });

    public static SyncEvent ToEvent(SyncOutboxEntry entry) =>
        new(entry.Id, entry.EntityType, entry.EntityId, entry.CreatedAt, entry.PayloadJson);

    // --- Fabriques de charges utiles ---------------------------------------

    public static SalePayload From(Sale sale, CustomerCredit? credit) => new(
        sale.Id, sale.Number, sale.IdempotencyKey, sale.CustomerId, sale.CashSessionId,
        sale.SellerName, sale.SubtotalXof, sale.DiscountXof, sale.TotalXof, sale.ChangeXof,
        sale.Status, sale.CreatedAt,
        [.. sale.Lines.Select(x => new SaleLineDto(x.VariantId, x.Sku, x.Description, x.Quantity, x.UnitPriceXof, x.FrozenUnitCostXof, x.DiscountXof, x.LineTotalXof))],
        [.. sale.Payments.Select(x => new PaymentDto(x.Id, x.Mode, x.AmountXof, x.ExternalReference, x.IsReversal))],
        credit is null ? null : new CreditPayload(credit.Id, credit.CustomerId, credit.OriginalAmountXof, credit.BalanceXof, credit.DueAt, credit.Status));

    public static CashSessionOpenedPayload Opened(CashSession session) =>
        new(session.Id, session.Number, session.OperatorName, session.OpeningFloatXof, session.OpenedAt);

    public static CashSessionClosedPayload Closed(CashSession session) => new(
        session.Id, session.Number, session.OperatorName, session.ClosedBy,
        session.OpeningFloatXof, session.ExpectedCashXof ?? 0, session.CountedCashXof ?? 0, session.DifferenceXof ?? 0,
        session.DifferenceReason, session.OpenedAt, session.ClosedAt ?? DateTimeOffset.UtcNow);

    public static CashMovementPayload From(CashMovement movement) =>
        new(movement.Id, movement.CashSessionId, movement.Direction, movement.AmountXof, movement.Reason, movement.Actor, movement.CreatedAt);

    public static ExpensePayload From(Expense expense) =>
        new(expense.Id, expense.Category, expense.Description, expense.AmountXof, expense.Mode, expense.CreatedAt);

    public static CreditPaymentPayload From(CreditPayment payment) =>
        new(payment.Id, payment.CustomerCreditId, payment.Number, payment.AmountXof, payment.Mode, payment.IsReversal, payment.ReversesPaymentId, payment.Actor, payment.CreatedAt);

    public static CustomerPayload From(Customer customer) => new(
        customer.Id, customer.Name, customer.Phone, customer.SecondaryPhone, customer.Gender,
        customer.Address, customer.Notes, customer.Preferences, customer.PreferredChannel,
        customer.CreditLimitXof, customer.MarketingConsent, customer.IsArchived, customer.CreatedAt, customer.UpdatedAt);

    public static StockMovementPayload From(StockMovement movement) =>
        new(movement.Id, movement.VariantId, movement.Type, movement.QuantityDelta, movement.UnitCostXof, movement.Reason, movement.SourceType, movement.SourceId, movement.Actor, movement.CreatedAt);
}
