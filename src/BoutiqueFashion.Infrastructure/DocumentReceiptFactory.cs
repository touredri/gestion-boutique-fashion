using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Infrastructure;

internal static class DocumentReceiptFactory
{
    private static readonly Dictionary<DocumentType, string> DefaultPrefixes = new()
    {
        [DocumentType.Receipt] = "TIC",
        [DocumentType.Invoice] = "FAC",
        [DocumentType.Proforma] = "PRO",
        [DocumentType.PaymentReceipt] = "REC",
        [DocumentType.DepositReceipt] = "DEP",
        [DocumentType.CreditPaymentReceipt] = "REC",
        [DocumentType.BalanceReceipt] = "SOL",
        [DocumentType.CreditNote] = "AVO",
        [DocumentType.ReturnNote] = "RET"
    };

    public static async Task<string> NextNumberAsync(BoutiqueDbContext db, DocumentType type, CancellationToken cancellationToken)
    {
        var prefix = await db.AppSettings.Where(x => x.Key == $"Seq.{type}").Select(x => x.Value).SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(prefix)) prefix = DefaultPrefixes[type];
        var year = DateTimeOffset.UtcNow.Year;
        var sequence = await db.DocumentSequences.SingleOrDefaultAsync(x => x.Type == type && x.Year == year, cancellationToken);
        if (sequence is null)
        {
            sequence = new DocumentSequence { Type = type, Prefix = prefix, Year = year, NextValue = 2 };
            db.DocumentSequences.Add(sequence);
            return $"{prefix}-{year}-000001";
        }
        sequence.Prefix = prefix;
        var value = sequence.NextValue++;
        return $"{sequence.Prefix}-{year}-{value:000000}";
    }

    public static async Task<ReceiptData> CreateAsync(
        BoutiqueDbContext db,
        string number,
        string? customer,
        IReadOnlyList<ReceiptItem> items,
        long subtotalXof,
        long discountXof,
        long totalXof,
        IReadOnlyList<PaymentDraft> payments,
        string? footerOverride,
        CancellationToken cancellationToken,
        DocumentType type = DocumentType.Receipt,
        long changeXof = 0)
    {
        async Task<string> Setting(string key, string fallback = "") =>
            await db.AppSettings.Where(x => x.Key == key).Select(x => x.Value).SingleOrDefaultAsync(cancellationToken) ?? fallback;

        var receipt = new ReceiptData(
            await Setting("Shop.Name", "Ma Boutique"),
            await Setting("Shop.Address"),
            await Setting("Shop.Phone"),
            number,
            DateTimeOffset.UtcNow,
            customer,
            items,
            subtotalXof,
            discountXof,
            totalXof,
            payments,
            footerOverride ?? await Setting("Shop.Footer", "Merci de votre visite"),
            false,
            await Setting("Shop.Email"),
            await Setting("Shop.TaxId"),
            await Setting("Shop.Slogan"),
            await Setting("Shop.Logo"),
            await Setting("Shop.Stamp"),
            await Setting("Shop.Signature"),
            await Setting("Shop.ReturnPolicy"),
            changeXof,
            Kind: type);
        return await ApplyTemplateAsync(db, type, receipt, cancellationToken);
    }

    public static async Task<ReceiptData> ApplyTemplateAsync(BoutiqueDbContext db, DocumentType type, ReceiptData receipt, CancellationToken cancellationToken)
    {
        async Task<bool> Flag(string element) =>
            (await db.AppSettings.Where(x => x.Key == $"Doc.{type}.{element}").Select(x => x.Value).SingleOrDefaultAsync(cancellationToken) ?? "1") == "1";

        var styleValue = await db.AppSettings.Where(x => x.Key == $"Doc.{type}.Style").Select(x => x.Value).SingleOrDefaultAsync(cancellationToken);
        var style = Enum.TryParse<DocumentStyle>(styleValue, true, out var parsed) ? parsed : DocumentStyle.Moderne;
        return receipt with
        {
            LogoPath = await Flag("Logo") ? receipt.LogoPath : null,
            Slogan = await Flag("Slogan") ? receipt.Slogan : null,
            StampPath = await Flag("Stamp") ? receipt.StampPath : null,
            SignaturePath = await Flag("Signature") ? receipt.SignaturePath : null,
            Style = style
        };
    }

    public static Task<ReceiptData> BuildSampleAsync(BoutiqueDbContext db, DocumentType type, CancellationToken cancellationToken) =>
        CreateAsync(db, $"APERCU-{type.ToString().ToUpperInvariant()}", "Client exemple",
            [new ReceiptItem("Article exemple - Noir / M", 2, 15_000, 3_000, 27_000)],
            30_000, 3_000, 27_000,
            [new PaymentDraft(PaymentMode.Cash, 30_000)],
            "Aperçu du modèle sans vente réelle", cancellationToken, type, 3_000);
}
