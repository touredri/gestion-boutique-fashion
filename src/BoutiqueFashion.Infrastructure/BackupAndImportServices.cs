using System.Globalization;
using System.Text;
using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Infrastructure;

public sealed class BackupService(AppPaths paths, IAuthorizationService authorization) : IBackupService
{
    public async Task<string> CreateAsync(CancellationToken cancellationToken = default)
    {
        var destination = Path.Combine(paths.Backups, $"boutique-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        await using var connection = new SqliteConnection($"Data Source={paths.Database}");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $destination";
        command.Parameters.AddWithValue("$destination", destination);
        await command.ExecuteNonQueryAsync(cancellationToken);
        foreach (var file in Directory.GetFiles(paths.Backups, "boutique-*.db").OrderByDescending(File.GetCreationTimeUtc).Skip(30)) File.Delete(file);
        return destination;
    }

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Directory.GetFiles(paths.Backups, "boutique-*.db").OrderByDescending(File.GetCreationTimeUtc).ToArray());

    public async Task<bool> VerifyAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return false;
        await using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return string.Equals((string?)await command.ExecuteScalarAsync(cancellationToken), "ok", StringComparison.OrdinalIgnoreCase);
    }

    public async Task RestoreAsync(string path, string managerPin, CancellationToken cancellationToken = default)
    {
        if (!await authorization.AuthorizeSensitiveActionAsync(managerPin, "Restaurer sauvegarde", cancellationToken: cancellationToken)) throw new UnauthorizedAccessException("PIN responsable invalide.");
        if (!await VerifyAsync(path, cancellationToken)) throw new InvalidDataException("La sauvegarde est invalide.");
        var safety = await CreateAsync(cancellationToken);
        SqliteConnection.ClearAllPools();
        try { File.Copy(path, paths.Database, true); }
        catch { File.Copy(safety, paths.Database, true); throw; }
    }
}

public sealed class ProductImportService(IDbContextFactory<BoutiqueDbContext> factory) : IProductImportService
{
    private static readonly string[] Headers = ["produit", "categorie", "marque", "sku", "code_barres", "taille", "couleur", "cout_xof", "prix_xof", "quantite", "seuil_alerte"];

    public async Task<ImportPreview> PreviewAsync(string csvPath, CancellationToken cancellationToken = default)
    {
        var lines = await File.ReadAllLinesAsync(csvPath, Encoding.UTF8, cancellationToken);
        var rows = new List<ImportRow>(); var issues = new List<ImportIssue>();
        if (lines.Length == 0) return new ImportPreview(rows, [new ImportIssue(1, "Le fichier est vide.")]);
        var header = ParseLine(lines[0]).Select(x => x.Trim().ToLowerInvariant()).ToArray();
        if (!Headers.SequenceEqual(header)) return new ImportPreview(rows, [new ImportIssue(1, $"En-tête attendu: {string.Join(',', Headers)}")]);
        for (var index = 1; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) continue;
            try
            {
                var c = ParseLine(lines[index]);
                if (c.Count != Headers.Length) throw new FormatException("Nombre de colonnes incorrect.");
                rows.Add(new ImportRow(c[0], c[1], Null(c[2]), c[3], Null(c[4]), Null(c[5]), Null(c[6]), Long(c[7]), Long(c[8]), Decimal(c[9]), Decimal(c[10])));
            }
            catch (Exception exception) when (exception is FormatException or OverflowException) { issues.Add(new ImportIssue(index + 1, exception.Message)); }
        }
        issues.AddRange(rows.GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).Select(x => new ImportIssue(0, $"SKU dupliqué dans le fichier: {x.Key}")));
        return new ImportPreview(rows, issues);
    }

    public async Task<int> ImportAsync(ImportPreview preview, CancellationToken cancellationToken = default)
    {
        if (preview.Issues.Count != 0) throw new InvalidOperationException("Corrigez les erreurs avant l'import.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var skus = preview.Rows.Select(x => x.Sku).ToArray();
        if (await db.ProductVariants.AnyAsync(x => skus.Contains(x.Sku), cancellationToken)) throw new InvalidOperationException("Un SKU du fichier existe déjà.");
        foreach (var row in preview.Rows)
        {
            var category = await db.Categories.SingleOrDefaultAsync(x => x.Name == row.Category, cancellationToken) ?? new Category { Name = row.Category };
            var product = await db.Products.SingleOrDefaultAsync(x => x.Name == row.Product && x.CategoryId == category.Id, cancellationToken) ?? new Product { Name = row.Product, Brand = row.Brand, Category = category, CategoryId = category.Id };
            var variant = new ProductVariant { Product = product, ProductId = product.Id, Sku = row.Sku, Barcode = row.Barcode, Size = row.Size, Color = row.Color, CostXof = row.CostXof, WeightedAverageCostXof = row.CostXof, PriceXof = row.PriceXof, QuantityOnHand = row.Quantity, LowStockThreshold = row.AlertThreshold };
            db.ProductVariants.Add(variant);
            if (row.Quantity != 0) db.StockMovements.Add(new StockMovement { Variant = variant, Type = StockMovementType.Inventory, QuantityDelta = row.Quantity, UnitCostXof = row.CostXof, Reason = "Import stock initial", SourceType = "CsvImport", Actor = "Responsable" });
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return preview.Rows.Count;
    }

    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static long Long(string value) => long.Parse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
    private static decimal Decimal(string value) => decimal.Parse(value.Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture);
    private static List<string> ParseLine(string line)
    {
        var result = new List<string>(); var buffer = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"' && quoted && i + 1 < line.Length && line[i + 1] == '"') { buffer.Append('"'); i++; }
            else if (ch == '"') quoted = !quoted;
            else if (ch == ',' && !quoted) { result.Add(buffer.ToString()); buffer.Clear(); }
            else buffer.Append(ch);
        }
        if (quoted) throw new FormatException("Guillemets non fermés.");
        result.Add(buffer.ToString()); return result;
    }
}
