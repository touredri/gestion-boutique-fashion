using BoutiqueFashion.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace BoutiqueFashion.Server.Sync;

internal sealed record UserContext(Guid UserId, string Username, string DisplayName);

/// <summary>
/// Authentification des comptes de pilotage : identifiant et mot de passe, puis jeton de session.
///
/// Jeton opaque plutôt que JWT. Un JWT ne se révoque pas avant son expiration : un téléphone
/// perdu resterait connecté jusqu'à l'échéance. Ici, une ligne supprimée coupe l'accès
/// immédiatement, et le coût est une lecture indexée par requête.
/// </summary>
internal static class UserAuthentication
{
    public const string Scheme = "Bearer ";
    /// <summary>Assez long pour qu'on ne se reconnecte pas chaque matin, assez court pour qu'un
    /// téléphone égaré finisse par se fermer tout seul.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    private const int MaxFailedAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    public static async Task<UserContext?> ResolveAsync(HttpContext context, ServerDbContext db, CancellationToken cancellationToken)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith(Scheme, StringComparison.Ordinal)) return null;

        var hash = Passwords.HashToken(header[Scheme.Length..].Trim());
        var session = await db.UserSessions.Include(x => x.User)
            .SingleOrDefaultAsync(x => x.TokenHash == hash && x.RevokedAt == null, cancellationToken);
        if (session is null || session.ExpiresAt < DateTimeOffset.UtcNow) return null;
        if (session.User is null || !session.User.IsActive) return null;

        return new UserContext(session.User.Id, session.User.Username, session.User.DisplayName);
    }

    public static async Task<(string Token, DateTimeOffset ExpiresAt, User User)?> LoginAsync(
        ServerDbContext db, string username, string password, CancellationToken cancellationToken)
    {
        var normalized = (username ?? string.Empty).Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(x => x.Username == normalized, cancellationToken);

        // Même vérification quand le compte n'existe pas : renvoyer immédiatement laisserait
        // deviner, au temps de réponse, quels identifiants existent.
        var valid = Passwords.Verify(password, user?.PasswordHash ?? Passwords.Hash("dummy-comparison-target"));
        if (user is null || !user.IsActive) return null;

        if (user.LockedUntil is { } until && until > DateTimeOffset.UtcNow) return null;

        if (!valid)
        {
            user.FailedAttempts++;
            if (user.FailedAttempts >= MaxFailedAttempts)
            {
                user.LockedUntil = DateTimeOffset.UtcNow.Add(LockoutDuration);
                user.FailedAttempts = 0;
            }
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }

        user.FailedAttempts = 0;
        user.LockedUntil = null;
        user.LastLoginAt = DateTimeOffset.UtcNow;

        var token = Passwords.CreateSessionToken();
        var session = new UserSession
        {
            UserId = user.Id,
            TokenHash = Passwords.HashToken(token),
            ExpiresAt = DateTimeOffset.UtcNow.Add(SessionLifetime),
        };
        db.UserSessions.Add(session);

        // Ménage opportuniste : sans lui la table gonflerait indéfiniment de sessions mortes.
        var stale = await db.UserSessions
            .Where(x => x.UserId == user.Id && (x.ExpiresAt < DateTimeOffset.UtcNow || x.RevokedAt != null))
            .ToListAsync(cancellationToken);
        db.UserSessions.RemoveRange(stale);

        await db.SaveChangesAsync(cancellationToken);
        return (token, session.ExpiresAt, user);
    }

    /// <summary>
    /// Crée le premier compte à partir de la configuration si la base n'en a aucun. Sans cela,
    /// un serveur fraîchement déployé serait inaccessible à tout le monde, y compris à sa
    /// propriétaire.
    /// </summary>
    public static async Task EnsureFirstUserAsync(ServerDbContext db, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (await db.Users.AnyAsync(cancellationToken)) return;

        var username = configuration["Bootstrap:Username"];
        var password = configuration["Bootstrap:Password"];
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return;

        Passwords.Validate(password);
        db.Users.Add(new User
        {
            Username = username.Trim().ToLowerInvariant(),
            DisplayName = configuration["Bootstrap:DisplayName"] ?? username.Trim(),
            PasswordHash = Passwords.Hash(password),
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
