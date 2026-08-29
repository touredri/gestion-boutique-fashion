using System.Text.Json;
using BoutiqueFashion.Application;
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
        if (sensitiveDiscount && (draft.ManagerPin is null || !await authorization.AuthorizeSensitiveActionAsync(draft.ManagerPin, "Remise supérieure à 10 %", cancellationToken: cancellationToken)))
            throw new UnauthorizedAccessException("PIN responsable requis pour cette remise.");
        if (hasCredit && (draft.ManagerPin is null || !await authorization.AuthorizeSensitiveActionAsync(draft.ManagerPin, "Vente à crédit", cancellationToken: cancellationToken)))
            throw new UnauthorizedAccessException("PIN responsable requis pour le crédit.");

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var previous = await db.Sales.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == draft.IdempotencyKey, cancellationToken);
        if (previous is not null)
        {
            var documentId = await db.DocumentSnapshots.Where(x => x.SaleId == previous.Id).Select(x => x.Id).SingleAsync(cancellationToken);
            return new SaleResult(previous.Id, previous.Number, previous.TotalXof, documentId, true, false);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var cashSession = await db.CashSessions.SingleOrDefaultAsync(x => x.Status == CashSessionStatus.Open, cancellationToken)
            ?? throw new InvalidOperationException("Ouvrez la caisse avant d'enregistrer une vente.");
        var ids = draft.Lines.Select(x => x.VariantId).Distinct().ToArray();
        var variants = await db.ProductVariants.Include(x => x.Product).Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        if (variants.Count != ids.Length) throw new KeyNotFoundException("Un article du panier est introuvable.");

        var sale = new Sale { IdempotencyKey = draft.IdempotencyKey, CashSessionId = cashSession.Id, CustomerId = draft.CustomerId };
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
        if (draft.Payments.Sum(x => x.AmountXof) != sale.TotalXof) throw new InvalidOperationException("La somme des paiements doit être égale au total de la vente.");
        if (draft.Payments.Any(x => x.AmountXof < 0)) throw new InvalidOperationException("Un paiement ne peut pas être négatif.");

        Customer? customer = null;
        if (draft.CustomerId is not null) customer = await db.Customers.SingleOrDefaultAsync(x => x.Id == draft.CustomerId, cancellationToken) ?? throw new KeyNotFoundException("Client introuvable.");
        if (hasCredit)
        {
            if (customer is null || draft.CreditDueAt is null) throw new InvalidOperationException("Un client et une échéance sont obligatoires pour le crédit.");
            var outstanding = await db.CustomerCredits.Where(x => x.CustomerId == customer.Id && x.Status != CreditStatus.Paid && x.Status != CreditStatus.Cancelled).SumAsync(x => x.BalanceXof, cancellationToken);
            var creditAmount = draft.Payments.Where(x => x.Mode == PaymentMode.Credit).Sum(x => x.AmountXof);
            if (customer.CreditLimitXof <= 0 || outstanding + creditAmount > customer.CreditLimitXof) throw new InvalidOperationException("Le plafond de crédit du client est dépassé.");
            db.CustomerCredits.Add(new CustomerCredit { SaleId = sale.Id, CustomerId = customer.Id, OriginalAmountXof = creditAmount, BalanceXof = creditAmount, DueAt = draft.CreditDueAt.Value });
        }

        sale.Number = await NextNumberAsync(db, DocumentType.Receipt, "TIC", cancellationToken);
        foreach (var payment in draft.Payments)
            sale.Payments.Add(new Payment { Mode = payment.Mode, AmountXof = payment.AmountXof, ExternalReference = payment.Reference });
        var negativeStock = false;
        foreach (var line in sale.Lines)
        {
            var variant = variants[line.VariantId];
            variant.QuantityOnHand -= line.Quantity;
            negativeStock |= variant.QuantityOnHand < 0;
            db.StockMovements.Add(new StockMovement { VariantId = variant.Id, Type = StockMovementType.Sale, QuantityDelta = -line.Quantity, UnitCostXof = line.FrozenUnitCostXof, Reason = $"Vente {sale.Number}", SourceType = nameof(Sale), SourceId = sale.Id });
        }
        db.Sales.Add(sale);
        var shopName = await SettingAsync(db, "Shop.Name", "Ma Boutique", cancellationToken);
        var address = await SettingAsync(db, "Shop.Address", string.Empty, cancellationToken);
        var phone = await SettingAsync(db, "Shop.Phone", string.Empty, cancellationToken);
        var footer = await SettingAsync(db, "Shop.Footer", "Merci de votre visite", cancellationToken);
        var email = await SettingAsync(db,"Shop.Email",string.Empty,cancellationToken);var taxId=await SettingAsync(db,"Shop.TaxId",string.Empty,cancellationToken);var slogan=await SettingAsync(db,"Shop.Slogan",string.Empty,cancellationToken);var logo=await SettingAsync(db,"Shop.Logo",string.Empty,cancellationToken);var stamp=await SettingAsync(db,"Shop.Stamp",string.Empty,cancellationToken);var signature=await SettingAsync(db,"Shop.Signature",string.Empty,cancellationToken);var returnPolicy=await SettingAsync(db,"Shop.ReturnPolicy",string.Empty,cancellationToken);
        var receipt = new ReceiptData(shopName, address, phone, sale.Number, DateTimeOffset.UtcNow, customer?.Name,
            sale.Lines.Select(x => new ReceiptItem(x.Description, x.Quantity, x.UnitPriceXof, x.DiscountXof, x.LineTotalXof)).ToArray(), sale.SubtotalXof + sale.Lines.Sum(x => x.DiscountXof), sale.DiscountXof, sale.TotalXof, draft.Payments, footer,false,email,taxId,slogan,logo,stamp,signature,returnPolicy);
        var snapshot = new DocumentSnapshot { SaleId = sale.Id, Type = DocumentType.Receipt, Number = sale.Number, JsonPayload = JsonSerializer.Serialize(receipt) };
        db.DocumentSnapshots.Add(snapshot);
        db.AuditEntries.Add(new AuditEntry { Actor = "Vendeur boutique", Action = "Créer vente", EntityType = nameof(Sale), EntityId = sale.Id.ToString(), AfterJson = JsonSerializer.Serialize(new { sale.Number, sale.TotalXof }) });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SaleResult(sale.Id, sale.Number, sale.TotalXof, snapshot.Id, false, negativeStock);
    }

    private static string BuildDescription(ProductVariant variant) => string.Join(" - ", new[] { variant.Product?.Name, variant.Color, variant.Size }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static async Task<string> NextNumberAsync(BoutiqueDbContext db, DocumentType type, string prefix, CancellationToken cancellationToken)
    {
        var year = DateTimeOffset.UtcNow.Year;
        var sequence = await db.DocumentSequences.SingleOrDefaultAsync(x => x.Type == type && x.Year == year, cancellationToken);
        if (sequence is null) { sequence = new DocumentSequence { Type = type, Prefix = prefix, Year = year, NextValue = 2 }; db.DocumentSequences.Add(sequence); return $"{prefix}-{year}-000001"; }
        var value = sequence.NextValue++;
        return $"{sequence.Prefix}-{year}-{value:000000}";
    }

    private static async Task<string> SettingAsync(BoutiqueDbContext db, string key, string fallback, CancellationToken cancellationToken) =>
        await db.AppSettings.Where(x => x.Key == key).Select(x => x.Value).SingleOrDefaultAsync(cancellationToken) ?? fallback;
}

public sealed class CashSessionService(IDbContextFactory<BoutiqueDbContext> factory) : ICashSessionService
{
    public async Task<CashSession> OpenAsync(long openingFloatXof, CancellationToken cancellationToken = default)
    {
        if (openingFloatXof < 0) throw new ArgumentOutOfRangeException(nameof(openingFloatXof));
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (await db.CashSessions.AnyAsync(x => x.Status == CashSessionStatus.Open, cancellationToken)) throw new InvalidOperationException("Une caisse est déjà ouverte.");
        var session = new CashSession { Number = $"CAI-{DateTime.Now:yyyyMMdd-HHmmss}", OpeningFloatXof = openingFloatXof };
        db.CashSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session;
    }

    public async Task<CashSession?> GetOpenAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.CashSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Status == CashSessionStatus.Open, cancellationToken);
    }

    public async Task<CashSession> CloseAsync(long countedCashXof, string? differenceReason, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var session = await db.CashSessions.Include(x => x.Sales).ThenInclude(x => x.Payments).SingleOrDefaultAsync(x => x.Status == CashSessionStatus.Open, cancellationToken) ?? throw new InvalidOperationException("Aucune caisse ouverte.");
        var saleCash = session.Sales.SelectMany(x => x.Payments).Where(x => x.Mode == PaymentMode.Cash).Sum(x => x.AmountXof);
        var creditCash = await db.CreditPayments.Where(x => x.CreatedAt >= session.OpenedAt && x.Mode == PaymentMode.Cash).SumAsync(x => x.AmountXof, cancellationToken);
        var cashExpenses = await db.Expenses.Where(x => x.CreatedAt >= session.OpenedAt && x.Mode == PaymentMode.Cash).SumAsync(x => x.AmountXof, cancellationToken);
        var expected = session.OpeningFloatXof + saleCash + creditCash - cashExpenses;
        var difference = countedCashXof - expected;
        if (difference != 0 && string.IsNullOrWhiteSpace(differenceReason)) throw new InvalidOperationException("Un motif est obligatoire en cas d'écart.");
        session.ExpectedCashXof = expected; session.CountedCashXof = countedCashXof; session.DifferenceXof = difference; session.DifferenceReason = differenceReason; session.ClosedAt = DateTimeOffset.UtcNow; session.Status = CashSessionStatus.Closed;
        await db.SaveChangesAsync(cancellationToken);
        return session;
    }
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
        var soldLines = await db.SaleLines.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt < to && x.Sale!.Status == SaleStatus.Completed).Select(x => new { x.Quantity, x.FrozenUnitCostXof }).ToListAsync(cancellationToken);
        var cost = soldLines.Sum(x => decimal.ToInt64(decimal.Round(x.Quantity * x.FrozenUnitCostXof, 0)));
        var expenses = await db.Expenses.Where(x => x.CreatedAt >= from && x.CreatedAt < to).SumAsync(x => x.AmountXof, cancellationToken);
        var credit = await db.CustomerCredits.Where(x => x.Status != CreditStatus.Paid && x.Status != CreditStatus.Cancelled).SumAsync(x => x.BalanceXof, cancellationToken);
        var low = await db.ProductVariants.CountAsync(x => x.IsActive && x.QuantityOnHand <= x.LowStockThreshold, cancellationToken);
        return new DashboardSummary(salesXof, collected, salesXof - cost, expenses, credit, low);
    }

    public async Task<IReadOnlyList<ReportRow>> SalesByPaymentModeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var saleRows = await db.Payments.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt < to).GroupBy(x => x.Mode).Select(x => new { Mode = x.Key, Value = x.Sum(y => y.AmountXof) }).ToListAsync(cancellationToken);
        var creditRows = await db.CreditPayments.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt < to).GroupBy(x => x.Mode).Select(x => new { Mode = x.Key, Value = x.Sum(y => y.AmountXof) }).ToListAsync(cancellationToken);
        return saleRows.Concat(creditRows).GroupBy(x => x.Mode).Select(x => new ReportRow(x.Key.ToString(), x.Sum(y => y.Value))).Where(x => x.ValueXof != 0).OrderBy(x => x.Label).ToArray();
    }
}
