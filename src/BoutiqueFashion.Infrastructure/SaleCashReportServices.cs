using System.Text.Json;
using BoutiqueFashion.Application;
using BoutiqueFashion.Contracts;
using BoutiqueFashion.Domain;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Infrastructure;

public sealed class SaleService(IDbContextFactory<BoutiqueDbContext> factory, IAuthorizationService authorization) : ISaleService
{
    public async Task<SaleResult> CreateAsync(SaleDraft draft, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(draft.IdempotencyKey)) throw new ArgumentException("Une clé d'idempotence est obligatoire.");
        if (draft.Lines.Count == 0) throw new InvalidOperationException("Le panier est vide.");

        var sensitiveDiscount = draft.DiscountKind == DiscountKind.Percentage && draft.DiscountValue > BusinessRules.SellerDiscountLimitPercent;
        sensitiveDiscount |= draft.Lines.Any(x => x.DiscountKind == DiscountKind.Percentage && x.DiscountValue > BusinessRules.SellerDiscountLimitPercent);
        var hasCredit = draft.Payments.Any(x => x.Mode == PaymentMode.Credit && x.AmountXof > 0);
        if (draft.ReserveStock && !hasCredit)
            throw new InvalidOperationException("Une avance suppose un reste à payer : ajoutez une ligne de paiement « Crédit » pour le solde.");
        if (sensitiveDiscount && (draft.ManagerPin is null || !await authorization.AuthorizeSensitiveActionAsync(draft.ManagerPin, "Remise supérieure à 10 %", cancellationToken: cancellationToken)))
            throw new UnauthorizedAccessException("PIN responsable requis pour cette remise.");
        // L'avance réservée ne fait courir aucun risque : la marchandise ne quitte pas la boutique.
        // Le vendeur peut donc l'enregistrer seul, contrairement au crédit avec emport.
        if (hasCredit && !draft.ReserveStock && (draft.ManagerPin is null || !await authorization.AuthorizeSensitiveActionAsync(draft.ManagerPin, "Vente à crédit", cancellationToken: cancellationToken)))
            throw new UnauthorizedAccessException("PIN responsable requis pour le crédit.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var previous = await db.Sales.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == draft.IdempotencyKey, cancellationToken);
        if (previous is not null)
        {
            var documentId = await db.DocumentSnapshots.Where(x => x.SaleId == previous.Id && x.Type == DocumentType.Receipt).Select(x => x.Id).SingleAsync(cancellationToken);
            var invoiceId = await db.DocumentSnapshots.Where(x => x.SaleId == previous.Id && x.Type == DocumentType.Invoice).Select(x => (Guid?)x.Id).FirstOrDefaultAsync(cancellationToken);
            return new SaleResult(previous.Id, previous.Number, previous.TotalXof, documentId, true, false, previous.ChangeXof, invoiceId);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var cashSession = await db.CashSessions.SingleOrDefaultAsync(x => x.Status == CashSessionStatus.Open, cancellationToken)
            ?? throw new InvalidOperationException("Ouvrez la caisse avant d'enregistrer une vente.");
        var ids = draft.Lines.Select(x => x.VariantId).Distinct().ToArray();
        var variants = await db.ProductVariants.Include(x => x.Product).Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        if (variants.Count != ids.Length) throw new KeyNotFoundException("Un article du panier est introuvable.");

        var sale = new Sale
        {
            IdempotencyKey = draft.IdempotencyKey,
            CashSessionId = cashSession.Id,
            CustomerId = draft.CustomerId,
            // Le vendeur n'est plus une constante : c'est la personne qui tient la vacation.
            SellerName = string.IsNullOrWhiteSpace(cashSession.OperatorName) ? "Vendeur boutique" : cashSession.OperatorName,
            Status = draft.ReserveStock ? SaleStatus.Reserved : SaleStatus.Completed,
        };
        long subtotal = 0;
        foreach (var lineDraft in draft.Lines)
        {
            if (lineDraft.Quantity <= 0) throw new InvalidOperationException("La quantité doit être positive.");
            var variant = variants[lineDraft.VariantId];
            if (!variant.IsActive) throw new InvalidOperationException($"{variant.Sku} est inactif.");
            var now = DateTimeOffset.UtcNow;
            var unitPrice = variant.PromotionalPriceXof is not null && variant.PromotionStartsAt <= now && variant.PromotionEndsAt >= now ? variant.PromotionalPriceXof.Value : variant.PriceXof;
            var gross = decimal.ToInt64(decimal.Round(unitPrice * lineDraft.Quantity, 0, MidpointRounding.AwayFromZero));
            var discount = BusinessRules.CalculateDiscount(gross, lineDraft.DiscountKind, lineDraft.DiscountValue);
            sale.Lines.Add(new SaleLine { VariantId = variant.Id, Description = BuildDescription(variant), Sku = variant.Sku, Quantity = lineDraft.Quantity, UnitPriceXof = unitPrice, FrozenUnitCostXof = decimal.ToInt64(decimal.Round(variant.WeightedAverageCostXof, 0)), DiscountXof = discount, LineTotalXof = gross - discount });
            subtotal += gross - discount;
        }
        var totalDiscount = BusinessRules.CalculateDiscount(subtotal, draft.DiscountKind, draft.DiscountValue);
        sale.SubtotalXof = subtotal;
        sale.DiscountXof = totalDiscount + sale.Lines.Sum(x => x.DiscountXof);
        sale.TotalXof = subtotal - totalDiscount;

        var sumPayments = draft.Payments.Sum(x => x.AmountXof);
        if (sumPayments < sale.TotalXof) throw new InvalidOperationException("La somme des paiements doit être égale au total de la vente.");
        if (draft.Payments.Any(x => x.AmountXof < 0)) throw new InvalidOperationException("Un paiement ne peut pas être négatif.");
        var change = sumPayments - sale.TotalXof;
        if (change > 0 && !draft.Payments.Any(x => x.Mode == PaymentMode.Cash && x.AmountXof >= change))
            throw new InvalidOperationException("La monnaie rendue doit être couverte par un paiement en espèces.");
        sale.ChangeXof = change;

        Customer? customer = null;
        if (draft.CustomerId is not null) customer = await db.Customers.SingleOrDefaultAsync(x => x.Id == draft.CustomerId, cancellationToken) ?? throw new KeyNotFoundException("Client introuvable.");
        else if (!string.IsNullOrWhiteSpace(draft.NewCustomerName) || !string.IsNullOrWhiteSpace(draft.NewCustomerPhone))
        {
            var phone = string.IsNullOrWhiteSpace(draft.NewCustomerPhone) ? null : draft.NewCustomerPhone.Trim();
            if (phone is not null) customer = await db.Customers.SingleOrDefaultAsync(x => x.Phone == phone, cancellationToken);
            if (customer is null)
            {
                customer = new Customer { Name = string.IsNullOrWhiteSpace(draft.NewCustomerName) ? $"Client {phone}" : draft.NewCustomerName.Trim(), Phone = phone };
                db.Customers.Add(customer);
                // Le client créé au vol doit remonter avant la vente qui le référence.
                Outbox.Enqueue(db, SyncEntityTypes.Customer, customer.Id, Outbox.From(customer));
            }
            sale.CustomerId = customer.Id;
        }
        var creditAmount = draft.Payments.Where(x => x.Mode == PaymentMode.Credit).Sum(x => x.AmountXof);
        CustomerCredit? credit = null;
        if (hasCredit)
        {
            if (customer is null || draft.CreditDueAt is null) throw new InvalidOperationException("Un client et une échéance sont obligatoires pour le crédit.");
            // Le plafond protège contre la marchandise partie sans être payée. Une avance réservée
            // n'expose à rien : exiger un plafond y interdirait le cas d'usage le plus courant,
            // celui du client de passage qui bloque un article et revient le solder.
            if (!draft.ReserveStock)
            {
                var outstanding = await db.CustomerCredits.Where(x => x.CustomerId == customer.Id && x.Status != CreditStatus.Paid && x.Status != CreditStatus.Cancelled).SumAsync(x => x.BalanceXof, cancellationToken);
                if (customer.CreditLimitXof <= 0 || outstanding + creditAmount > customer.CreditLimitXof) throw new InvalidOperationException("Le plafond de crédit du client est dépassé.");
            }
            credit = new CustomerCredit { SaleId = sale.Id, CustomerId = customer.Id, OriginalAmountXof = creditAmount, BalanceXof = creditAmount, DueAt = draft.CreditDueAt.Value };
            db.CustomerCredits.Add(credit);
        }

        sale.Number = await DocumentReceiptFactory.NextNumberAsync(db, DocumentType.Receipt, cancellationToken);
        var remainingChange = change;
        foreach (var payment in draft.Payments)
        {
            var amount = payment.AmountXof;
            if (remainingChange > 0 && payment.Mode == PaymentMode.Cash)
            {
                var deducted = Math.Min(remainingChange, amount);
                amount -= deducted;
                remainingChange -= deducted;
            }
            if (amount > 0) sale.Payments.Add(new Payment { Mode = payment.Mode, AmountXof = amount, ExternalReference = payment.Reference });
        }
        var negativeStock = false;
        foreach (var line in sale.Lines)
        {
            var variant = variants[line.VariantId];
            if (draft.ReserveStock)
            {
                // La marchandise reste en boutique : QuantityOnHand ne bouge pas, seule la part
                // réservée grandit. On ne peut pas mettre de côté ce qu'on n'a pas — contrairement
                // à une vente normale, qui tolère le stock négatif à régulariser.
                if (variant.QuantityAvailable < line.Quantity)
                    throw new InvalidOperationException($"{variant.Sku} : {variant.QuantityAvailable:0.##} disponible(s), impossible d'en réserver {line.Quantity:0.##}.");
                variant.QuantityReserved += line.Quantity;
                db.StockMovements.Add(new StockMovement { VariantId = variant.Id, Type = StockMovementType.Reservation, QuantityDelta = -line.Quantity, UnitCostXof = line.FrozenUnitCostXof, Reason = $"Mise de côté {sale.Number}", SourceType = nameof(Sale), SourceId = sale.Id, Actor = sale.SellerName });
            }
            else
            {
                variant.QuantityOnHand -= line.Quantity;
                negativeStock |= variant.QuantityOnHand < 0;
                db.StockMovements.Add(new StockMovement { VariantId = variant.Id, Type = StockMovementType.Sale, QuantityDelta = -line.Quantity, UnitCostXof = line.FrozenUnitCostXof, Reason = $"Vente {sale.Number}", SourceType = nameof(Sale), SourceId = sale.Id, Actor = sale.SellerName });
            }
        }
        db.Sales.Add(sale);

        var items = sale.Lines.Select(x => new ReceiptItem(x.Description, x.Quantity, x.UnitPriceXof, x.DiscountXof, x.LineTotalXof)).ToArray();
        var receiptSubtotal = sale.SubtotalXof + sale.Lines.Sum(x => x.DiscountXof);
        var snapshot = new DocumentSnapshot { SaleId = sale.Id, Type = DocumentType.Receipt, Number = sale.Number, JsonPayload = JsonSerializer.Serialize(await DocumentReceiptFactory.CreateAsync(db, sale.Number, customer?.Name, items, receiptSubtotal, sale.DiscountXof, sale.TotalXof, draft.Payments, null, cancellationToken, DocumentType.Receipt, change)) };
        db.DocumentSnapshots.Add(snapshot);

        var invoiceNumber = await DocumentReceiptFactory.NextNumberAsync(db, DocumentType.Invoice, cancellationToken);
        var invoiceSnapshot = new DocumentSnapshot { SaleId = sale.Id, Type = DocumentType.Invoice, Number = invoiceNumber, JsonPayload = JsonSerializer.Serialize(await DocumentReceiptFactory.CreateAsync(db, invoiceNumber, customer?.Name, items, receiptSubtotal, sale.DiscountXof, sale.TotalXof, draft.Payments, null, cancellationToken, DocumentType.Invoice, change)) };
        db.DocumentSnapshots.Add(invoiceSnapshot);

        if (creditAmount > 0 && sale.TotalXof - creditAmount > 0)
        {
            var deposit = sale.TotalXof - creditAmount;
            var depositNumber = await DocumentReceiptFactory.NextNumberAsync(db, DocumentType.DepositReceipt, cancellationToken);
            var depositFooter = draft.ReserveStock
                ? $"Reste à payer : {creditAmount:N0} FCFA · Marchandise réservée en boutique jusqu'au solde."
                : $"Reste à payer : {creditAmount:N0} FCFA";
            db.DocumentSnapshots.Add(new DocumentSnapshot { SaleId = sale.Id, Type = DocumentType.DepositReceipt, Number = depositNumber, JsonPayload = JsonSerializer.Serialize(await DocumentReceiptFactory.CreateAsync(db, depositNumber, customer?.Name, [new ReceiptItem($"Acompte sur vente {sale.Number}", 1, deposit, 0, deposit)], deposit, 0, deposit, draft.Payments.Where(x => x.Mode != PaymentMode.Credit).ToArray(), depositFooter, cancellationToken, DocumentType.DepositReceipt)) });
        }

        db.AuditEntries.Add(new AuditEntry { Actor = sale.SellerName, Action = draft.ReserveStock ? "Créer avance réservée" : "Créer vente", EntityType = nameof(Sale), EntityId = sale.Id.ToString(), AfterJson = JsonSerializer.Serialize(new { sale.Number, sale.TotalXof, change, customer = customer?.Name, reserved = draft.ReserveStock }) });
        // La vente part en un seul événement, lignes et paiements compris : le serveur ne doit
        // jamais voir une vente sans son détail, ni l'inverse.
        Outbox.Enqueue(db, SyncEntityTypes.Sale, sale.Id, Outbox.From(sale, credit));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SaleResult(sale.Id, sale.Number, sale.TotalXof, snapshot.Id, false, negativeStock, change, invoiceSnapshot.Id);
    }

    private static string BuildDescription(ProductVariant variant) => string.Join(" - ", new[] { variant.Product?.Name, variant.Color, variant.Size }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

public sealed class CashSessionService(IDbContextFactory<BoutiqueDbContext> factory, IAuthorizationService authorization) : ICashSessionService
{
    public async Task<CashSession> OpenAsync(long openingFloatXof, string? operatorName = null, string? operatorPin = null, CancellationToken cancellationToken = default)
    {
        if (openingFloatXof < 0) throw new ArgumentOutOfRangeException(nameof(openingFloatXof));
        if (!string.IsNullOrEmpty(operatorPin)) PinHasher.Validate(operatorPin, nameof(operatorPin));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (await db.CashSessions.AnyAsync(x => x.Status == CashSessionStatus.Open, cancellationToken)) throw new InvalidOperationException("Une caisse est déjà ouverte.");

        // À défaut de nom saisi, la boutique elle-même tient la caisse : c'est ce nom qui
        // apparaîtra sur les ventes, et il vaut mieux « Boutique Marcory » que « Vendeur boutique ».
        var shopName = await db.AppSettings.Where(x => x.Key == "Shop.Name").Select(x => x.Value).SingleOrDefaultAsync(cancellationToken);
        var name = Coalesce(operatorName, shopName, "Vendeur boutique");

        var session = new CashSession
        {
            Number = $"CAI-{DateTime.Now:yyyyMMdd-HHmmss}",
            OpeningFloatXof = openingFloatXof,
            OperatorName = name,
            OperatorPinHash = string.IsNullOrEmpty(operatorPin) ? null : PinHasher.Hash(operatorPin),
        };
        db.CashSessions.Add(session);
        Outbox.Enqueue(db, SyncEntityTypes.CashSessionOpened, session.Id, Outbox.Opened(session));
        db.AuditEntries.Add(new AuditEntry { Actor = name, Action = "Ouvrir caisse", EntityType = nameof(CashSession), EntityId = session.Id.ToString(), AfterJson = JsonSerializer.Serialize(new { session.Number, openingFloatXof, hasPin = session.OperatorPinHash is not null }) });
        await db.SaveChangesAsync(cancellationToken);
        return session;
    }

    public async Task<CashSession?> GetOpenAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.CashSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Status == CashSessionStatus.Open, cancellationToken);
    }

    public async Task<bool> VerifyShiftPinAsync(string pin, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var hash = await db.CashSessions.AsNoTracking().Where(x => x.Status == CashSessionStatus.Open).Select(x => x.OperatorPinHash).SingleOrDefaultAsync(cancellationToken);
        return PinHasher.Verify(pin, hash);
    }

    public async Task<CashDeskState?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var session = await db.CashSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Status == CashSessionStatus.Open, cancellationToken);
        if (session is null) return null;
        var totals = await ComputeAsync(db, session, cancellationToken);

        var byMode = await db.Payments.AsNoTracking()
            .Where(x => x.Sale!.CashSessionId == session.Id)
            .GroupBy(x => x.Mode)
            .Select(g => new { Mode = g.Key, Value = g.Sum(y => y.AmountXof) })
            .ToListAsync(cancellationToken);

        var salesCount = await db.Sales.AsNoTracking().CountAsync(x => x.CashSessionId == session.Id && x.Status != SaleStatus.Cancelled, cancellationToken);
        var salesTotal = await db.Sales.AsNoTracking().Where(x => x.CashSessionId == session.Id && x.Status != SaleStatus.Cancelled).SumAsync(x => x.TotalXof, cancellationToken);

        var movements = await db.CashMovements.AsNoTracking()
            .Where(x => x.CashSessionId == session.Id)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new CashMovementRow(x.Id, x.Direction, x.AmountXof, x.Reason, x.Actor, x.CreatedAt))
            .ToListAsync(cancellationToken);

        return new CashDeskState(
            session.Id, session.Number, session.OperatorName, session.OpenedAt, session.OperatorPinHash is not null,
            session.OpeningFloatXof, totals.SaleCash, totals.CreditCash, totals.CashExpenses,
            totals.Expected, salesCount, salesTotal,
            byMode.Where(x => x.Value != 0).Select(x => new ReportRow(Libelles.Text(x.Mode), x.Value)).OrderBy(x => x.Label).ToArray(),
            totals.MovementsIn, totals.MovementsOut, movements);
    }

    public async Task<CashSession> CloseAsync(long countedCashXof, string? differenceReason, string? pin = null, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var session = await db.CashSessions.SingleOrDefaultAsync(x => x.Status == CashSessionStatus.Open, cancellationToken) ?? throw new InvalidOperationException("Aucune caisse ouverte.");

        var totals = await ComputeAsync(db, session, cancellationToken);
        var difference = countedCashXof - totals.Expected;
        if (difference != 0 && string.IsNullOrWhiteSpace(differenceReason)) throw new InvalidOperationException("Un motif est obligatoire en cas d'écart.");

        var toleranceSetting = await db.AppSettings.Where(x => x.Key == "Cash.VarianceToleranceXof").Select(x => x.Value).SingleOrDefaultAsync(cancellationToken);
        var tolerance = long.TryParse(toleranceSetting, out var parsedTolerance) ? parsedTolerance : 0;
        var beyondTolerance = Math.Abs(difference) > tolerance;

        // Qui clôture ? Le PIN de vacation suffit au quotidien ; l'écart hors tolérance reste une
        // décision du gérant, comme avant. Le code gérant n'est vérifié que s'il peut servir :
        // le tester systématiquement inscrirait un refus à l'audit à chaque clôture ordinaire.
        var isShiftPin = PinHasher.Verify(pin, session.OperatorPinHash);
        var isManagerPin = (!isShiftPin || beyondTolerance)
            && !string.IsNullOrEmpty(pin)
            && await authorization.AuthorizeSensitiveActionAsync(pin, "Clôturer la caisse", cancellationToken: cancellationToken);

        if (session.OperatorPinHash is not null && !isShiftPin && !isManagerPin)
            throw new UnauthorizedAccessException("Code de vacation ou code gérant requis pour clôturer la caisse.");
        if (beyondTolerance && !isManagerPin)
            throw new UnauthorizedAccessException($"Écart de {difference:N0} FCFA au-delà de la tolérance ({tolerance:N0} FCFA) : code gérant requis.");

        var closedBy = isManagerPin ? "Responsable" : session.OperatorName;
        session.ExpectedCashXof = totals.Expected; session.CountedCashXof = countedCashXof; session.DifferenceXof = difference;
        session.DifferenceReason = differenceReason; session.ClosedAt = DateTimeOffset.UtcNow; session.Status = CashSessionStatus.Closed;
        session.ClosedBy = closedBy;
        Outbox.Enqueue(db, SyncEntityTypes.CashSessionClosed, session.Id, Outbox.Closed(session));
        db.AuditEntries.Add(new AuditEntry { Actor = closedBy, Action = "Clôturer caisse", EntityType = nameof(CashSession), EntityId = session.Id.ToString(), AfterJson = JsonSerializer.Serialize(new { expected = totals.Expected, countedCashXof, difference, operator_ = session.OperatorName }) });
        await db.SaveChangesAsync(cancellationToken);
        return session;
    }

    /// <summary>Espèces attendues en tiroir. Partagé entre l'affichage temps réel et la clôture :
    /// deux formules divergentes feraient apparaître un écart au moment de compter.</summary>
    private static async Task<(long SaleCash, long CreditCash, long CashExpenses, long MovementsIn, long MovementsOut, long Expected)> ComputeAsync(BoutiqueDbContext db, CashSession session, CancellationToken cancellationToken)
    {
        var saleCash = await db.Payments.Where(x => x.Sale!.CashSessionId == session.Id && x.Mode == PaymentMode.Cash).SumAsync(x => x.AmountXof, cancellationToken);
        var creditCash = await db.CreditPayments.Where(x => x.CreatedAt >= session.OpenedAt && x.Mode == PaymentMode.Cash).SumAsync(x => x.AmountXof, cancellationToken);
        var cashExpenses = await db.Expenses.Where(x => x.CreatedAt >= session.OpenedAt && x.Mode == PaymentMode.Cash).SumAsync(x => x.AmountXof, cancellationToken);
        var movementsIn = await db.CashMovements.Where(x => x.CashSessionId == session.Id && x.Direction == CashMovementDirection.In).SumAsync(x => x.AmountXof, cancellationToken);
        var movementsOut = await db.CashMovements.Where(x => x.CashSessionId == session.Id && x.Direction == CashMovementDirection.Out).SumAsync(x => x.AmountXof, cancellationToken);
        var expected = session.OpeningFloatXof + saleCash + creditCash - cashExpenses + movementsIn - movementsOut;
        return (saleCash, creditCash, cashExpenses, movementsIn, movementsOut, expected);
    }

    public async Task<CashMovement> RecordMovementAsync(CashMovementDirection direction, long amountXof, string reason, string? pin = null, CancellationToken cancellationToken = default)
    {
        if (amountXof <= 0) throw new ArgumentOutOfRangeException(nameof(amountXof), "Le montant doit être positif.");
        // Un mouvement sans motif est indiscernable d'un vol : c'est justement ce qu'on veut éviter.
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Le motif du mouvement est obligatoire.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var session = await db.CashSessions.SingleOrDefaultAsync(x => x.Status == CashSessionStatus.Open, cancellationToken)
            ?? throw new InvalidOperationException("Ouvrez la caisse avant d'enregistrer un mouvement d'espèces.");

        // Le plafond ne protège que les sorties : remettre de l'argent dans le tiroir n'expose à rien.
        if (direction == CashMovementDirection.Out)
        {
            var configured = await db.AppSettings.Where(x => x.Key == "Cash.MovementLimitXof").Select(x => x.Value).SingleOrDefaultAsync(cancellationToken);
            var limit = long.TryParse(configured, out var parsed) && parsed > 0 ? parsed : DefaultMovementLimitXof;
            if (amountXof > limit && (pin is null || !await authorization.AuthorizeSensitiveActionAsync(pin, $"Sortie d'espèces de {amountXof:N0} FCFA", cancellationToken: cancellationToken)))
                throw new UnauthorizedAccessException($"Sortie supérieure au plafond de {limit:N0} FCFA : code gérant requis.");
        }

        var actor = string.IsNullOrWhiteSpace(session.OperatorName) ? "Vendeur boutique" : session.OperatorName;
        var movement = new CashMovement { CashSessionId = session.Id, Direction = direction, AmountXof = amountXof, Reason = reason.Trim(), Actor = actor };
        db.CashMovements.Add(movement);
        Outbox.Enqueue(db, SyncEntityTypes.CashMovement, movement.Id, Outbox.From(movement));
        db.AuditEntries.Add(new AuditEntry { Actor = actor, Action = direction == CashMovementDirection.In ? "Entrée d'espèces" : "Sortie d'espèces", EntityType = nameof(CashMovement), EntityId = movement.Id.ToString(), AfterJson = JsonSerializer.Serialize(new { amountXof, reason, session.Number }) });
        await db.SaveChangesAsync(cancellationToken);
        return movement;
    }

    /// <summary>Couvre les achats de monnaie et petits appoints du quotidien ; au-delà, c'est la
    /// recette qui sort, et cela regarde la propriétaire.</summary>
    private const long DefaultMovementLimitXof = 25_000;

    private static string Coalesce(params string?[] candidates) =>
        candidates.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? string.Empty;
}

public sealed class ReportService(IDbContextFactory<BoutiqueDbContext> factory) : IReportService
{
    public async Task<DashboardSummary> DashboardAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var sales = db.Sales.AsNoTracking().Where(x => x.Status == SaleStatus.Completed && x.CreatedAt >= from && x.CreatedAt < to);
        var salesXof = await sales.SumAsync(x => x.TotalXof, cancellationToken);
        var saleCollected = await db.Payments.Where(x => x.CreatedAt >= from && x.CreatedAt < to && x.Mode != PaymentMode.Credit).SumAsync(x => x.AmountXof, cancellationToken);
        var creditCollected = await db.CreditPayments.Where(x => x.CreatedAt >= from && x.CreatedAt < to).SumAsync(x => x.AmountXof, cancellationToken);
        var collected = saleCollected + creditCollected;
        // Une seule lecture des lignes vendues sert au coût de revient et au meilleur article :
        // deux requêtes sur le même ensemble n'apporteraient rien.
        var soldLines = await db.SaleLines.AsNoTracking()
            .Where(x => x.CreatedAt >= from && x.CreatedAt < to && x.Sale!.Status == SaleStatus.Completed)
            // La variante peut avoir été supprimée depuis : on retombe alors sur la description
            // figée au moment de la vente, jamais sur du vide.
            .Select(x => new
            {
                x.Quantity,
                x.FrozenUnitCostXof,
                x.LineTotalXof,
                Name = x.Variant != null && x.Variant.Product != null ? x.Variant.Product.Name : x.Description,
            })
            .ToListAsync(cancellationToken);
        var cost = soldLines.Sum(x => decimal.ToInt64(decimal.Round(x.Quantity * x.FrozenUnitCostXof, 0)));
        var bestSeller = soldLines
            .GroupBy(x => x.Name)
            .Select(g => new BestSeller(g.Key, g.Sum(y => y.Quantity), g.Sum(y => y.LineTotalXof)))
            .OrderByDescending(x => x.Quantity).ThenByDescending(x => x.ValueXof)
            .FirstOrDefault();
        var expenses = await db.Expenses.Where(x => x.CreatedAt >= from && x.CreatedAt < to).SumAsync(x => x.AmountXof, cancellationToken);
        var credit = await db.CustomerCredits.Where(x => x.Status != CreditStatus.Paid && x.Status != CreditStatus.Cancelled).SumAsync(x => x.BalanceXof, cancellationToken);
        // Le seuil se juge sur le disponible : un article entièrement réservé est en rupture
        // commerciale, même si les cartons sont encore dans la réserve.
        var stocks = await db.ProductVariants.AsNoTracking().Where(x => x.IsActive).Select(x => new { x.QuantityOnHand, x.QuantityReserved, x.LowStockThreshold }).ToListAsync(cancellationToken);
        var low = stocks.Count(x => x.QuantityOnHand - x.QuantityReserved <= x.LowStockThreshold);
        var salesCount = await sales.CountAsync(cancellationToken);
        var grossMargin = salesXof - cost;
        return new DashboardSummary(salesXof, collected, grossMargin, expenses, credit, low, grossMargin - expenses, soldLines.Any(x => x.FrozenUnitCostXof <= 0), salesCount, bestSeller);
    }

    public async Task<IReadOnlyList<RecentSaleRow>> RecentSalesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Sales.AsNoTracking()
            .Where(x => x.CreatedAt >= from && x.CreatedAt < to && x.Status != SaleStatus.Cancelled)
            .OrderByDescending(x => x.CreatedAt).Take(8)
            .Select(x => new { x.Number, x.CreatedAt, x.TotalXof, Customer = x.Customer != null ? x.Customer.Name : null })
            .ToListAsync(cancellationToken);
        return rows.Select(x => new RecentSaleRow(x.Number, x.CreatedAt.ToLocalTime().ToString("HH:mm"), x.Customer ?? "Client de passage", x.TotalXof)).ToArray();
    }

    public async Task<IReadOnlyList<ReportRow>> SalesByPaymentModeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var saleRows = await db.Payments.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt < to).GroupBy(x => x.Mode).Select(x => new { Mode = x.Key, Value = x.Sum(y => y.AmountXof) }).ToListAsync(cancellationToken);
        var creditRows = await db.CreditPayments.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt < to).GroupBy(x => x.Mode).Select(x => new { Mode = x.Key, Value = x.Sum(y => y.AmountXof) }).ToListAsync(cancellationToken);
        return saleRows.Concat(creditRows).GroupBy(x => x.Mode).Select(x => new ReportRow(x.Key.ToString(), x.Sum(y => y.Value))).Where(x => x.ValueXof != 0).OrderBy(x => x.Label).ToArray();
    }

    public async Task<IReadOnlyList<ReportRow>> SalesByDayAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Sales.AsNoTracking().Where(x => x.Status == SaleStatus.Completed && x.CreatedAt >= from && x.CreatedAt < to).Select(x => new { x.CreatedAt, x.TotalXof }).ToListAsync(cancellationToken);
        return rows.GroupBy(x => x.CreatedAt.ToLocalTime().Date)
            .OrderBy(x => x.Key)
            .Select(x => new ReportRow(x.Key.ToString("dd/MM/yyyy"), x.Sum(y => y.TotalXof), x.Count()))
            .ToArray();
    }

    public async Task<IReadOnlyList<ReportRow>> SalesBySellerAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Sales.AsNoTracking().Where(x => x.Status == SaleStatus.Completed && x.CreatedAt >= from && x.CreatedAt < to)
            .GroupBy(x => x.SellerName)
            .Select(x => new ReportRow(x.Key, x.Sum(y => y.TotalXof), x.Count()))
            .OrderByDescending(x => x.ValueXof).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReportRow>> TopProductsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var lines = await db.SaleLines.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt < to && x.Sale!.Status == SaleStatus.Completed)
            .Select(x => new { x.Sku, x.LineTotalXof, x.Quantity }).ToListAsync(cancellationToken);
        return lines.GroupBy(x => x.Sku)
            .Select(g => new ReportRow(g.Key, g.Sum(y => y.LineTotalXof), g.Sum(y => y.Quantity)))
            .OrderByDescending(x => x.Quantity).Take(10).ToArray();
    }

    public async Task<IReadOnlyList<ReportRow>> TopProductsByProductAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var lines = await db.SaleLines.AsNoTracking()
            .Where(x => x.CreatedAt >= from && x.CreatedAt < to && x.Sale!.Status == SaleStatus.Completed)
            .Select(x => new
            {
                Name = x.Variant != null && x.Variant.Product != null ? x.Variant.Product.Name : x.Description,
                x.LineTotalXof,
                x.Quantity,
            })
            .ToListAsync(cancellationToken);
        return lines.GroupBy(x => x.Name)
            .Select(g => new ReportRow(g.Key, g.Sum(y => y.LineTotalXof), g.Sum(y => y.Quantity)))
            .OrderByDescending(x => x.Quantity).ThenByDescending(x => x.ValueXof).Take(10).ToArray();
    }

    public async Task<IReadOnlyList<ReportRow>> NoSalesProductsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var soldSkus = await db.SaleLines.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt < to && x.Sale!.Status == SaleStatus.Completed).Select(x => x.Sku).Distinct().ToListAsync(cancellationToken);
        return await db.ProductVariants.AsNoTracking().Where(x => x.IsActive && !soldSkus.Contains(x.Sku))
            .OrderBy(x => x.Product!.Name)
            .Select(x => new ReportRow(x.Product!.Name + " · " + x.Sku, 0, x.QuantityOnHand))
            .Take(100).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReportRow>> StockValueByCategoryAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.ProductVariants.AsNoTracking().Include(x => x.Product).ThenInclude(x => x!.Category).Where(x => x.IsActive).ToListAsync(cancellationToken);
        return rows.GroupBy(x => x.Product?.Category?.Name ?? "Sans catégorie")
            .Select(x => new ReportRow(x.Key, x.Sum(y => decimal.ToInt64(decimal.Round(y.QuantityOnHand * y.WeightedAverageCostXof, 0))), x.Sum(y => y.QuantityOnHand)))
            .OrderByDescending(x => x.ValueXof).ToArray();
    }

    public async Task<IReadOnlyList<ReportRow>> InventoryVarianceAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var movements = await db.StockMovements.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt < to && (x.Type == StockMovementType.Inventory || x.Type == StockMovementType.Adjustment || x.Type == StockMovementType.Damaged || x.Type == StockMovementType.Lost))
            .Select(x => new { Sku = x.Variant!.Sku, x.QuantityDelta, x.UnitCostXof }).ToListAsync(cancellationToken);
        return movements.GroupBy(x => x.Sku)
            .Select(g => new ReportRow(g.Key, g.Sum(y => decimal.ToInt64(decimal.Round(y.QuantityDelta * y.UnitCostXof, 0))), g.Sum(y => y.QuantityDelta)))
            .OrderBy(x => x.Label).ToArray();
    }

    public async Task<IReadOnlyList<ReportRow>> CorrectionsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var discounts = await db.Sales.AsNoTracking().Where(x => x.Status == SaleStatus.Completed && x.CreatedAt >= from && x.CreatedAt < to).SumAsync(x => x.DiscountXof, cancellationToken);
        var cancellations = await db.Sales.AsNoTracking().Where(x => x.Status == SaleStatus.Cancelled && x.CreatedAt >= from && x.CreatedAt < to).SumAsync(x => x.TotalXof, cancellationToken);
        var returnMovements = await db.StockMovements.AsNoTracking().Where(x => x.Type == StockMovementType.Return && x.CreatedAt >= from && x.CreatedAt < to).Select(x => new { x.QuantityDelta, x.UnitCostXof }).ToListAsync(cancellationToken);
        var returns = returnMovements.Sum(x => decimal.ToInt64(decimal.Round(x.QuantityDelta * x.UnitCostXof, 0)));
        return
        [
            new ReportRow("Remises accordées", discounts),
            new ReportRow("Ventes annulées", cancellations),
            new ReportRow("Retours (valeur coût)", returns)
        ];
    }

    public async Task<IReadOnlyList<ReportRow>> RotationAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var sold = await db.SaleLines.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt < to && x.Sale!.Status == SaleStatus.Completed)
            .GroupBy(x => x.Sku)
            .Select(x => new { Sku = x.Key, Sold = x.Sum(y => y.Quantity), Value = x.Sum(y => y.LineTotalXof) })
            .ToListAsync(cancellationToken);
        var variants = await db.ProductVariants.AsNoTracking().Include(x => x.Product).Where(x => x.IsActive).ToListAsync(cancellationToken);
        var rows = new List<ReportRow>();
        foreach (var variant in variants)
        {
            var soldQty = sold.FirstOrDefault(s => s.Sku == variant.Sku)?.Sold ?? 0;
            var value = sold.FirstOrDefault(s => s.Sku == variant.Sku)?.Value ?? 0;
            var averageStock = Math.Max(1, (variant.QuantityOnHand + soldQty) / 2);
            var rotation = Math.Round(soldQty / averageStock, 2);
            rows.Add(new ReportRow($"{variant.Product?.Name} · {variant.Sku}", value, rotation));
        }
        return rows.OrderBy(x => x.Quantity).ThenBy(x => x.Label).Take(100).ToArray();
    }

    public async Task<IReadOnlyList<CashClosingRow>> CashClosingsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.CashSessions.AsNoTracking().Where(x => x.Status == CashSessionStatus.Closed && x.ClosedAt != null && x.ClosedAt >= from && x.ClosedAt < to)
            .Select(x => new CashClosingRow(x.Number, x.OpenedAt, x.ClosedAt, x.ExpectedCashXof ?? 0, x.CountedCashXof ?? 0, x.DifferenceXof ?? 0, x.DifferenceReason))
            .ToListAsync(cancellationToken);
        return rows.OrderByDescending(x => x.ClosedAt).ToArray();
    }

    public async Task<IReadOnlyList<StockAlertRow>> StockAlertsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var active = await db.ProductVariants.AsNoTracking().Include(x => x.Product).Where(x => x.IsActive).ToListAsync(cancellationToken);
        var variants = active.Where(x => x.QuantityAvailable <= x.LowStockThreshold).OrderBy(x => x.QuantityAvailable).ToList();
        var negativeIds = variants.Where(x => x.QuantityOnHand < 0).Select(x => x.Id).ToArray();
        var relatedRows = await db.SaleLines.AsNoTracking().Where(x => negativeIds.Contains(x.VariantId))
            .Select(x => new { x.VariantId, x.CreatedAt, Number = x.Sale!.Number })
            .ToListAsync(cancellationToken);
        var relatedSales = relatedRows.GroupBy(x => x.VariantId)
            .Select(g => new { VariantId = g.Key, Number = g.OrderByDescending(y => y.CreatedAt).First().Number })
            .ToArray();
        return variants.Select(x => new StockAlertRow(
            x.Sku,
            x.Product?.Name ?? string.Empty,
            x.QuantityOnHand,
            x.LowStockThreshold,
            x.QuantityOnHand < 0 ? "Négatif (à fournir)" : x.QuantityOnHand == 0 ? "Rupture" : "Stock faible",
            x.QuantityOnHand < 0 ? relatedSales.FirstOrDefault(r => r.VariantId == x.Id)?.Number : null)).ToArray();
    }
}
