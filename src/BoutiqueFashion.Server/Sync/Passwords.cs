using System.Security.Cryptography;
using System.Text;

namespace BoutiqueFashion.Server.Sync;

/// <summary>
/// Hachage des mots de passe. PBKDF2-SHA256, mêmes paramètres que les codes du terminal : un
/// seul jeu de règles cryptographiques dans tout le produit, donc une seule chose à relever le
/// jour où le matériel aura rattrapé ce coût.
///
/// Le nombre d'itérations voyage avec l'empreinte : il pourra être augmenté sans invalider les
/// mots de passe déjà enregistrés.
/// </summary>
internal static class Passwords
{
    private const int Iterations = 210_000;

    public static void Validate(string password)
    {
        // Longueur plutôt que complexité imposée : une phrase longue résiste mieux qu'un
        // « P@ssw0rd! » que son propriétaire finira par coller sous le clavier.
        if (string.IsNullOrWhiteSpace(password) || password.Length < 10)
            throw new ArgumentException("Le mot de passe doit contenir au moins 10 caractères.", nameof(password));
    }

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(24);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string? password, string? encoded)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(encoded)) return false;
        try
        {
            var parts = encoded.Split('.');
            if (parts.Length != 3) return false;
            var iterations = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Jeton de session : 256 bits tirés au sort, stockés hachés. Une base volée ne
    /// donne donc aucune session utilisable.</summary>
    public static string CreateSessionToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
