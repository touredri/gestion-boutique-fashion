using BoutiqueFashion.Domain;
using BoutiqueFashion.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Server.Endpoints;

// --- Formes de réponse -----------------------------------------------------

/// <summary>Ce que la propriétaire regarde vingt fois par jour, pour une boutique.</summary>
public sealed record ShopOverview(
    Guid ShopId, string Name, string? City,
    bool IsCashOpen, string? OperatorName, DateTimeOffset? OpenedAt,
    long SalesXof, long CollectedXof, int SalesCount,
    long ExpensesXof, long OutstandingCreditXof,
    int LowStockCount, int ReservedAdvances,
    DateTimeOffset? LastSeenAt);

public sealed record Overview(
    DateTimeOffset From, DateTimeOffset To,
    long SalesXof, long CollectedXof, int SalesCount,
    IReadOnlyList<ShopOverview> Shops);

public sealed record LabelledAmount(string Label, long ValueXof, decimal Quantity = 0);

public sealed record BestSellerRow(string Label, decimal Quantity, long ValueXof);

public sealed record ReportSummary(
    DateTimeOffset From, DateTimeOffset To,
    long SalesXof, long CollectedXof, long CostXof, long GrossMarginXof,
    long ExpensesXof, long EstimatedProfitXof, long OutstandingCreditXof, int SalesCount,
    bool CostWarning,
    BestSellerRow? BestSeller,
    IReadOnlyList<LabelledAmount> ByDay,
    IReadOnlyList<LabelledAmount> ByShop,
    IReadOnlyList<LabelledAmount> ByOperator,
    IReadOnlyList<LabelledAmount> ByPaymentMode,
    IReadOnlyList<LabelledAmount> TopProducts);

public sealed record CashClosingRow(
    Guid Id, Guid ShopId, string ShopName, string Number, string OperatorName, string? ClosedBy,
    DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt,
    long OpeningFloatXof, long ExpectedCashXof, long CountedCashXof, long DifferenceXof, string? DifferenceReason);

public sealed record AdvanceRow(
    Guid Id, Guid ShopId, string ShopName, string CustomerName, string? CustomerPhone,
    string SaleNumber, bool IsReserved, long OriginalXof, long BalanceXof, DateTimeOffset DueAt, CreditStatus Status);

public sealed record StockRow(Guid VariantId, string Sku, string ProductName, string? Size, string? Color, decimal OnHand, decimal Reserved, decimal Available, decimal Threshold);

/// <summary>
/// Lectures de pilotage.
///
/// Toutes bornées par une période et, éventuellement, une boutique. Le chiffre d'affaires ne
/// compte que les ventes validées : une avance réservée n'est pas encore un revenu, sa
/// marchandise n'ayant pas quitté la boutique. L'acompte encaissé, lui, figure bien dans
/// l'argent collecté.
/// </summary>
internal static class Reporting
{
    public static RouteGroupBuilder MapReporting(this RouteGroupBuilder group)
    {
        group.MapGet("/overview", async (ServerDbContext db, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        {
            var (start, end) = Window(from, to);
            var shops = await db.Shops.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync(ct);
            var rows = new List<ShopOverview>(shops.Count);

            foreach (var shop in shops)
            {
                var sales = db.Sales.AsNoTracking().Where(x => x.ShopId == shop.Id && x.Status == SaleStatus.Completed && x.OccurredAt >= start && x.OccurredAt < end);
                var open = await db.CashSessions.AsNoTracking()
                    .Where(x => x.ShopId == shop.Id && !x.IsClosed)
                    .OrderByDescending(x => x.OpenedAt).FirstOrDefaultAsync(ct);

                // Encaissé : tout ce qui est réellement entré, y compris les acomptes d'avances
                // et les versements sur crédits, qui ne sont pas des ventes de la période.
                var collected = await db.SalePayments.AsNoTracking()
                    .Where(x => x.Mode != PaymentMode.Credit && db.Sales.Any(s => s.Id == x.SaleId && s.ShopId == shop.Id && s.OccurredAt >= start && s.OccurredAt < end))
                    .SumAsync(x => x.AmountXof, ct);
                collected += await db.CreditPayments.AsNoTracking()
                    .Where(x => x.ShopId == shop.Id && x.OccurredAt >= start && x.OccurredAt < end)
                    .SumAsync(x => x.AmountXof, ct);

                var stock = await db.ShopStocks.AsNoTracking().Where(x => x.ShopId == shop.Id)
                    .Join(db.Variants.Where(v => v.IsActive), s => s.VariantId, v => v.Id, (s, v) => new { Available = s.QuantityOnHand - s.QuantityReserved, v.LowStockThreshold })
                    .ToListAsync(ct);

                rows.Add(new ShopOverview(
                    shop.Id, shop.Name, shop.City,
                    open is not null, open?.OperatorName, open?.OpenedAt,
                    await sales.SumAsync(x => x.TotalXof, ct),
                    collected,
                    await sales.CountAsync(ct),
                    await db.Expenses.AsNoTracking().Where(x => x.ShopId == shop.Id && x.OccurredAt >= start && x.OccurredAt < end).SumAsync(x => x.AmountXof, ct),
                    await db.Credits.AsNoTracking().Where(x => x.ShopId == shop.Id && x.Status != CreditStatus.Paid && x.Status != CreditStatus.Cancelled).SumAsync(x => x.BalanceXof, ct),
                    stock.Count(x => x.Available <= x.LowStockThreshold),
                    await db.Sales.AsNoTracking().CountAsync(x => x.ShopId == shop.Id && x.Status == SaleStatus.Reserved, ct),
                    await db.Devices.AsNoTracking().Where(x => x.ShopId == shop.Id).MaxAsync(x => (DateTimeOffset?)x.LastSeenAt, ct)));
            }

            return Results.Ok(new Overview(start, end,
                rows.Sum(x => x.SalesXof), rows.Sum(x => x.CollectedXof), rows.Sum(x => x.SalesCount), rows));
        });

        group.MapGet("/reports", async (ServerDbContext db, DateTimeOffset? from, DateTimeOffset? to, Guid? shopId, CancellationToken ct) =>
        {
            var (start, end) = Window(from, to);
            var shopNames = await db.Shops.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);

            var sales = db.Sales.AsNoTracking()
                .Where(x => x.Status == SaleStatus.Completed && x.OccurredAt >= start && x.OccurredAt < end)
                .Where(x => shopId == null || x.ShopId == shopId);

            var salesXof = await sales.SumAsync(x => x.TotalXof, ct);
            var salesCount = await sales.CountAsync(ct);

            // Une seule lecture des lignes vendues sert au coût, au meilleur article et au
            // classement : trois requêtes sur le même ensemble n'apporteraient rien.
            var lines = await db.SaleLines.AsNoTracking()
                .Where(x => sales.Any(s => s.Id == x.SaleId))
                .Join(db.Variants, l => l.VariantId, v => v.Id, (l, v) => new { l.Quantity, l.FrozenUnitCostXof, l.LineTotalXof, l.Description, v.ProductId })
                .Join(db.Products, x => x.ProductId, p => p.Id, (x, p) => new { x.Quantity, x.FrozenUnitCostXof, x.LineTotalXof, Name = p.Name })
                .ToListAsync(ct);

            var cost = lines.Sum(x => decimal.ToInt64(decimal.Round(x.Quantity * x.FrozenUnitCostXof, 0)));
            var grossMargin = salesXof - cost;

            var expenses = await db.Expenses.AsNoTracking()
                .Where(x => x.OccurredAt >= start && x.OccurredAt < end)
                .Where(x => shopId == null || x.ShopId == shopId)
                .SumAsync(x => x.AmountXof, ct);

            var collected = await db.SalePayments.AsNoTracking()
                .Where(x => x.Mode != PaymentMode.Credit && sales.Any(s => s.Id == x.SaleId))
                .SumAsync(x => x.AmountXof, ct)
                + await db.CreditPayments.AsNoTracking()
                    .Where(x => x.OccurredAt >= start && x.OccurredAt < end)
                    .Where(x => shopId == null || x.ShopId == shopId)
                    .SumAsync(x => x.AmountXof, ct);

            var byProduct = lines.GroupBy(x => x.Name)
                .Select(g => new LabelledAmount(g.Key, g.Sum(y => y.LineTotalXof), g.Sum(y => y.Quantity)))
                .OrderByDescending(x => x.Quantity).ThenByDescending(x => x.ValueXof).ToList();

            var perDay = await sales.Select(x => new { x.OccurredAt, x.TotalXof }).ToListAsync(ct);
            var perShop = await sales.GroupBy(x => x.ShopId).Select(g => new { ShopId = g.Key, Value = g.Sum(x => x.TotalXof), Count = g.Count() }).ToListAsync(ct);
            var perOperator = await sales.GroupBy(x => x.SellerName).Select(g => new { Name = g.Key, Value = g.Sum(x => x.TotalXof), Count = g.Count() }).ToListAsync(ct);
            var perMode = await db.SalePayments.AsNoTracking().Where(x => sales.Any(s => s.Id == x.SaleId))
                .GroupBy(x => x.Mode).Select(g => new { Mode = g.Key, Value = g.Sum(x => x.AmountXof) }).ToListAsync(ct);

            return Results.Ok(new ReportSummary(
                start, end, salesXof, collected, cost, grossMargin,
                expenses, grossMargin - expenses,
                await db.Credits.AsNoTracking().Where(x => x.Status != CreditStatus.Paid && x.Status != CreditStatus.Cancelled)
                    .Where(x => shopId == null || x.ShopId == shopId).SumAsync(x => x.BalanceXof, ct),
                salesCount,
                // Un coût de revient nul fausse la marge : mieux vaut le dire que d'afficher un
                // bénéfice flatteur et faux.
                lines.Any(x => x.FrozenUnitCostXof <= 0),
                byProduct.Count > 0 ? new BestSellerRow(byProduct[0].Label, byProduct[0].Quantity, byProduct[0].ValueXof) : null,
                [.. perDay.GroupBy(x => x.OccurredAt.ToLocalTime().Date).OrderBy(x => x.Key)
                    .Select(g => new LabelledAmount(g.Key.ToString("yyyy-MM-dd"), g.Sum(y => y.TotalXof), g.Count()))],
                [.. perShop.Select(x => new LabelledAmount(shopNames.GetValueOrDefault(x.ShopId, "Boutique"), x.Value, x.Count)).OrderByDescending(x => x.ValueXof)],
                [.. perOperator.Select(x => new LabelledAmount(x.Name, x.Value, x.Count)).OrderByDescending(x => x.ValueXof)],
                [.. perMode.Where(x => x.Value != 0).Select(x => new LabelledAmount(Libelles.Text(x.Mode), x.Value)).OrderByDescending(x => x.ValueXof)],
                [.. byProduct.Take(10)]));
        });

        group.MapGet("/cash-closings", async (ServerDbContext db, DateTimeOffset? from, DateTimeOffset? to, Guid? shopId, CancellationToken ct) =>
        {
            var (start, end) = Window(from, to);
            // Filtre composé en C# et non inline : « shopId == null || ... » place une constante
            // nulle dans l'arbre d'expression, qu'EF ne sait plus traduire une fois jointe.
            var sessions = db.CashSessions.AsNoTracking().Where(x => x.IsClosed && x.ClosedAt >= start && x.ClosedAt < end);
            if (shopId is { } only) sessions = sessions.Where(x => x.ShopId == only);

            // Tri et découpe sur les colonnes, projection en dernier : EF ne sait pas trier
            // sur les propriétés d'un record déjà projeté.
            var rows = await sessions
                .Join(db.Shops, s => s.ShopId, shop => shop.Id, (s, shop) => new { Session = s, Shop = shop })
                .OrderByDescending(x => x.Session.ClosedAt)
                .Take(200)
                .Select(x => new CashClosingRow(
                    x.Session.Id, x.Session.ShopId, x.Shop.Name, x.Session.Number, x.Session.OperatorName, x.Session.ClosedBy,
                    x.Session.OpenedAt, x.Session.ClosedAt, x.Session.OpeningFloatXof,
                    x.Session.ExpectedCashXof ?? 0, x.Session.CountedCashXof ?? 0, x.Session.DifferenceXof ?? 0, x.Session.DifferenceReason))
                .ToListAsync(ct);

            return Results.Ok(rows);
        });

        group.MapGet("/advances", async (ServerDbContext db, Guid? shopId, bool includeSettled, CancellationToken ct) =>
        {
            var credits = db.Credits.AsNoTracking().AsQueryable();
            if (shopId is { } only) credits = credits.Where(x => x.ShopId == only);
            if (!includeSettled) credits = credits.Where(x => x.Status != CreditStatus.Paid && x.Status != CreditStatus.Cancelled);

            var rows = await credits
                .Join(db.Shops, c => c.ShopId, s => s.Id, (c, s) => new { Credit = c, Shop = s })
                .Join(db.Customers, x => x.Credit.CustomerId, c => c.Id, (x, c) => new { x.Credit, x.Shop, Customer = c })
                .Join(db.Sales, x => x.Credit.SaleId, s => s.Id, (x, s) => new { x.Credit, x.Shop, x.Customer, Sale = s })
                .OrderBy(x => x.Credit.DueAt)
                .Take(500)
                .Select(x => new AdvanceRow(
                    x.Credit.Id, x.Credit.ShopId, x.Shop.Name, x.Customer.Name, x.Customer.Phone,
                    x.Sale.Number, x.Sale.Status == SaleStatus.Reserved,
                    x.Credit.OriginalAmountXof, x.Credit.BalanceXof, x.Credit.DueAt, x.Credit.Status))
                .ToListAsync(ct);

            return Results.Ok(rows);
        });

        group.MapGet("/shops/{shopId:guid}/stock-detail", async (Guid shopId, ServerDbContext db, bool lowOnly, CancellationToken ct) =>
        {
            var joined = db.ShopStocks.AsNoTracking().Where(x => x.ShopId == shopId)
                .Join(db.Variants.Where(v => v.IsActive), s => s.VariantId, v => v.Id, (s, v) => new { s, v })
                .Join(db.Products, x => x.v.ProductId, p => p.Id, (x, p) => new { x.s, x.v, Product = p });

            // Le filtre porte sur les colonnes, pas sur le record projeté : EF ne sait pas relire
            // les propriétés d'une projection pour en refaire une clause SQL.
            if (lowOnly) joined = joined.Where(x => x.s.QuantityOnHand - x.s.QuantityReserved <= x.v.LowStockThreshold);

            var rows = await joined
                .OrderBy(x => x.s.QuantityOnHand - x.s.QuantityReserved).ThenBy(x => x.Product.Name)
                .Take(1000)
                .Select(x => new StockRow(
                    x.v.Id, x.v.Sku, x.Product.Name, x.v.Size, x.v.Color,
                    x.s.QuantityOnHand, x.s.QuantityReserved, x.s.QuantityOnHand - x.s.QuantityReserved,
                    x.v.LowStockThreshold))
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        return group;
    }

    /// <summary>Par défaut la journée en cours, bornes locales : c'est ce que veut dire
    /// « aujourd'hui » pour une commerçante, pas les 24 dernières heures UTC.</summary>
    private static (DateTimeOffset From, DateTimeOffset To) Window(DateTimeOffset? from, DateTimeOffset? to)
    {
        if (from is not null && to is not null) return (from.Value, to.Value);
        var now = DateTimeOffset.Now;
        var start = new DateTimeOffset(now.Date, now.Offset);
        return (from ?? start, to ?? start.AddDays(1));
    }
}
