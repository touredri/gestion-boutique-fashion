using System.Text.Json;
using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Infrastructure;

/// <summary>
/// Les brouillons vivent dans AppSettings, en JSON, sous la clé « Draft.Product.{id} ».
/// Deux raisons : un produit non validé ne doit apparaître ni au catalogue ni au stock,
/// et la base étant créée par EnsureCreated, aucune colonne ni table nouvelle ne serait
/// ajoutée aux installations existantes.
/// </summary>
public sealed class ProductDraftService(IDbContextFactory<BoutiqueDbContext> factory) : IProductDraftService
{
    private const string Prefix = "Draft.Product.";

    public async Task<Guid> SubmitAsync(ProductDraft draft, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(draft.ProductName)) throw new ArgumentException("Le nom du produit est obligatoire.");
        if (draft.Lines.Count == 0) throw new ArgumentException("Ajoutez au moins une variante.");
        var id = draft.Id == Guid.Empty ? Guid.NewGuid() : draft.Id;
        var stored = draft with { Id = id };
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var key = Prefix + id.ToString("N");
        var setting = await db.AppSettings.SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        var json = JsonSerializer.Serialize(stored);
        if (setting is null) db.AppSettings.Add(new AppSetting { Key = key, Value = json });
        else { setting.Value = json; setting.UpdatedAt = DateTimeOffset.UtcNow; }
        db.AuditEntries.Add(new AuditEntry { Actor = "Vendeur", Action = "Proposer un produit", EntityType = "ProductDraft", EntityId = id.ToString(), AfterJson = json });
        await db.SaveChangesAsync(cancellationToken);
        return id;
    }

    public async Task<IReadOnlyList<ProductDraft>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await db.AppSettings.AsNoTracking().Where(x => x.Key.StartsWith(Prefix)).Select(x => x.Value).ToListAsync(cancellationToken);
        var drafts = new List<ProductDraft>();
        foreach (var row in rows)
        {
            // Un brouillon illisible ne doit pas empêcher d'afficher les autres.
            try { if (JsonSerializer.Deserialize<ProductDraft>(row) is { } draft) drafts.Add(draft); }
            catch (JsonException) { }
        }
        return drafts.OrderBy(x => x.CreatedAt).ToArray();
    }

    public async Task DeleteAsync(Guid draftId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var key = Prefix + draftId.ToString("N");
        var setting = await db.AppSettings.SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (setting is null) return;
        db.AppSettings.Remove(setting);
        db.AuditEntries.Add(new AuditEntry { Actor = "Responsable", Action = "Clore un brouillon produit", EntityType = "ProductDraft", EntityId = draftId.ToString() });
        await db.SaveChangesAsync(cancellationToken);
    }
}
