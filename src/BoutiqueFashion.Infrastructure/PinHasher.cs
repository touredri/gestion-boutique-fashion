using System.Security.Cryptography;

namespace BoutiqueFashion.Infrastructure;

/// <summary>
/// Hachage PBKDF2 des codes à chiffres. Deux codes coexistent désormais : le PIN gérant,
/// permanent et stocké dans AppSettings, et le PIN de vacation, choisi à chaque ouverture de
/// caisse et stocké sur la session.
///
/// Le code vivait dans <see cref="AuthorizationService"/>. Il en sort pour que la caisse puisse
/// vérifier un PIN de vacation sans dupliquer les paramètres cryptographiques : deux copies
/// finiraient par diverger, et la plus faible des deux deviendrait la sécurité réelle du système.
/// </summary>
internal static class PinHasher
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 24;
    private const int HashBytes = 32;

    /// <summary>Règle commune aux deux codes : 4 à 12 chiffres, rien d'autre.</summary>
    public static void Validate(string pin, string paramName = "pin")
    {
        if (string.IsNullOrEmpty(pin) || pin.Length is < 4 or > 12 || pin.Any(c => !char.IsDigit(c)))
            throw new ArgumentException("Le code doit contenir entre 4 et 12 chiffres.", paramName);
    }

    /// <summary>Encodé « itérations.sel.empreinte » — le nombre d'itérations voyage avec
    /// l'empreinte pour qu'on puisse le relever un jour sans invalider les codes existants.</summary>
    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string? pin, string? encoded)
    {
        if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(encoded)) return false;
        try
        {
            var parts = encoded.Split('.');
            if (parts.Length != 3) return false;
            var iterations = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}
