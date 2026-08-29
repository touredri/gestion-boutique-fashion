using BoutiqueFashion.Application;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Infrastructure;

internal static class DocumentReceiptFactory
{
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
        CancellationToken cancellationToken)
    {
        async Task<string> Setting(string key, string fallback = "") =>
            await db.AppSettings.Where(x => x.Key == key).Select(x => x.Value).SingleOrDefaultAsync(cancellationToken) ?? fallback;

        return new ReceiptData(
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
            await Setting("Shop.ReturnPolicy"));
    }
}
