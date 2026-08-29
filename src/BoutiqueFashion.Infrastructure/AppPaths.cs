namespace BoutiqueFashion.Infrastructure;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BoutiqueFashion");
        Data = Path.Combine(Root, "data");
        Backups = Path.Combine(Root, "backups");
        Documents = Path.Combine(Root, "documents");
        Assets = Path.Combine(Root, "assets");
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Backups);
        Directory.CreateDirectory(Documents);
        Directory.CreateDirectory(Assets);
    }

    public string Root { get; }
    public string Data { get; }
    public string Backups { get; }
    public string Documents { get; }
    public string Assets { get; }
    public string Database => Path.Combine(Data, "boutique.db");
}

