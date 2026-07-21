using System.Security.Cryptography;
using System.Text;

namespace SharedWorlds.Backend.Identity;

public readonly record struct StewardSessionId(Guid Value)
{
    public static StewardSessionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public sealed record StewardSessionOptions
{
    public StewardSessionOptions(TimeSpan accessLifetime, TimeSpan refreshLifetime)
    {
        if (accessLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(accessLifetime));
        }

        if (refreshLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(refreshLifetime));
        }

        if (refreshLifetime <= accessLifetime)
        {
            throw new ArgumentException(
                "Refresh lifetime must be longer than access lifetime.",
                nameof(refreshLifetime));
        }

        AccessLifetime = accessLifetime;
        RefreshLifetime = refreshLifetime;
    }

    public TimeSpan AccessLifetime { get; }
    public TimeSpan RefreshLifetime { get; }

    public static StewardSessionOptions FirstReleaseDefaults { get; } =
        new(TimeSpan.FromMinutes(15), TimeSpan.FromDays(30));
}

public sealed record StewardSessionRecord(
    StewardSessionId Id,
    VerifiedExternalIdentity Identity,
    string InstallationId,
    string RefreshTokenHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset RefreshExpiresAt,
    bool Revoked = false,
    DateTimeOffset? RevokedAt = null);

public sealed record StewardAccessCredentialRecord(
    string AccessTokenHash,
    StewardSessionId SessionId,
    DateTimeOffset ExpiresAt);

public sealed record StewardAccessContext(
    StewardSessionRecord Session,
    StewardAccessCredentialRecord AccessCredential);

public enum StoreRotateRefreshSessionStatus
{
    Rotated,
    InvalidCredential
}

/// <summary>
/// Persistence boundary for Steward authentication sessions. Authentication credentials and World
/// reservation generations are deliberately separate authorities.
/// </summary>
public interface IStewardSessionStore
{
    Task ReplaceInstallationSessionAsync(
        StewardSessionRecord session,
        StewardAccessCredentialRecord accessCredential,
        DateTimeOffset replacedAt,
        CancellationToken cancellationToken = default);

    Task<StewardAccessContext?> LoadAccessContextAsync(
        string accessTokenHash,
        CancellationToken cancellationToken = default);

    Task<StewardSessionRecord?> LoadRefreshSessionAsync(
        string refreshTokenHash,
        CancellationToken cancellationToken = default);

    Task<StoreRotateRefreshSessionStatus> TryRotateRefreshSessionAsync(
        StewardSessionId sessionId,
        string expectedRefreshTokenHash,
        string installationId,
        string newRefreshTokenHash,
        DateTimeOffset newRefreshExpiresAt,
        StewardAccessCredentialRecord newAccessCredential,
        DateTimeOffset rotatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryRevokeRefreshSessionAsync(
        string refreshTokenHash,
        string installationId,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default);
}

public interface IStewardSessionTokenGenerator
{
    string CreateAccessToken();
    string CreateRefreshToken();
}

public sealed class CryptographicStewardSessionTokenGenerator : IStewardSessionTokenGenerator
{
    public string CreateAccessToken() => "st_access_" + CreateSecret();
    public string CreateRefreshToken() => "st_refresh_" + CreateSecret();

    private static string CreateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

public sealed record StewardSessionTokens(
    string AccessToken,
    DateTimeOffset AccessExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt);

public sealed record StewardAuthenticatedCaller(
    VerifiedExternalIdentity Identity,
    string InstallationId,
    StewardSessionId SessionId);

public enum RefreshStewardSessionStatus
{
    Refreshed,
    InvalidCredential
}

public sealed record RefreshStewardSessionResult(
    RefreshStewardSessionStatus Status,
    StewardSessionTokens? Tokens);

/// <summary>
/// Issues and validates opaque Steward credentials after external identity verification succeeds.
/// The server stores only SHA-256 token hashes; plaintext credentials exist only in the response to
/// the client that must protect its refresh credential using OS-protected storage.
/// </summary>
public sealed class StewardSessionService
{
    private readonly IStewardSessionStore _store;
    private readonly IStewardSessionTokenGenerator _tokens;
    private readonly StewardSessionOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<StewardSessionId> _newSessionId;

    public StewardSessionService(
        IStewardSessionStore store,
        Func<DateTimeOffset> utcNow,
        StewardSessionOptions? options = null,
        IStewardSessionTokenGenerator? tokenGenerator = null,
        Func<StewardSessionId>? newSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(utcNow);
        _store = store;
        _utcNow = utcNow;
        _options = options ?? StewardSessionOptions.FirstReleaseDefaults;
        _tokens = tokenGenerator ?? new CryptographicStewardSessionTokenGenerator();
        _newSessionId = newSessionId ?? StewardSessionId.New;
    }

    public async Task<StewardSessionTokens> CreateSessionAsync(
        VerifiedExternalIdentity identity,
        string installationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateInstallationId(installationId);

        var now = _utcNow();
        var sessionId = _newSessionId();
        if (sessionId.Value == Guid.Empty)
        {
            throw new InvalidOperationException("Session ID generator returned an empty ID.");
        }

        var accessToken = _tokens.CreateAccessToken();
        var refreshToken = _tokens.CreateRefreshToken();
        ValidateGeneratedToken(accessToken, "access");
        ValidateGeneratedToken(refreshToken, "refresh");
        EnsureDifferentTokens(accessToken, refreshToken);

        var accessExpiresAt = now + _options.AccessLifetime;
        var refreshExpiresAt = now + _options.RefreshLifetime;
        var session = new StewardSessionRecord(
            sessionId,
            identity,
            installationId,
            HashToken(refreshToken),
            now,
            refreshExpiresAt);
        var access = new StewardAccessCredentialRecord(
            HashToken(accessToken),
            sessionId,
            accessExpiresAt);

        await _store.ReplaceInstallationSessionAsync(
            session,
            access,
            now,
            cancellationToken);

        return new StewardSessionTokens(
            accessToken,
            accessExpiresAt,
            refreshToken,
            refreshExpiresAt);
    }

    public async Task<StewardAuthenticatedCaller?> ValidateAccessTokenAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var context = await _store.LoadAccessContextAsync(
            HashToken(accessToken),
            cancellationToken);
        if (context is null)
        {
            return null;
        }

        var now = _utcNow();
        if (context.Session.Revoked ||
            context.Session.RefreshExpiresAt <= now ||
            context.AccessCredential.ExpiresAt <= now)
        {
            return null;
        }

        if (context.AccessCredential.SessionId != context.Session.Id)
        {
            throw new InvalidOperationException("Session store returned mismatched access context.");
        }

        return new StewardAuthenticatedCaller(
            context.Session.Identity,
            context.Session.InstallationId,
            context.Session.Id);
    }

    public async Task<RefreshStewardSessionResult> RefreshAsync(
        string refreshToken,
        string installationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(installationId))
        {
            return new(RefreshStewardSessionStatus.InvalidCredential, null);
        }

        var now = _utcNow();
        var refreshHash = HashToken(refreshToken);
        var session = await _store.LoadRefreshSessionAsync(refreshHash, cancellationToken);
        if (session is null ||
            session.Revoked ||
            session.RefreshExpiresAt <= now ||
            !string.Equals(session.InstallationId, installationId, StringComparison.Ordinal))
        {
            return new(RefreshStewardSessionStatus.InvalidCredential, null);
        }

        var newAccessToken = _tokens.CreateAccessToken();
        var newRefreshToken = _tokens.CreateRefreshToken();
        ValidateGeneratedToken(newAccessToken, "access");
        ValidateGeneratedToken(newRefreshToken, "refresh");
        EnsureDifferentTokens(newAccessToken, newRefreshToken);

        var accessExpiresAt = now + _options.AccessLifetime;
        var refreshExpiresAt = now + _options.RefreshLifetime;
        var accessRecord = new StewardAccessCredentialRecord(
            HashToken(newAccessToken),
            session.Id,
            accessExpiresAt);

        var rotated = await _store.TryRotateRefreshSessionAsync(
            session.Id,
            refreshHash,
            installationId,
            HashToken(newRefreshToken),
            refreshExpiresAt,
            accessRecord,
            now,
            cancellationToken);
        if (rotated != StoreRotateRefreshSessionStatus.Rotated)
        {
            return new(RefreshStewardSessionStatus.InvalidCredential, null);
        }

        return new(
            RefreshStewardSessionStatus.Refreshed,
            new StewardSessionTokens(
                newAccessToken,
                accessExpiresAt,
                newRefreshToken,
                refreshExpiresAt));
    }

    public Task<bool> RevokeAsync(
        string refreshToken,
        string installationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(installationId))
        {
            return Task.FromResult(false);
        }

        return _store.TryRevokeRefreshSessionAsync(
            HashToken(refreshToken),
            installationId,
            _utcNow(),
            cancellationToken);
    }

    internal static string HashToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static void ValidateInstallationId(string installationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        if (installationId.Length > 256)
        {
            throw new ArgumentOutOfRangeException(
                nameof(installationId),
                "Installation ID is too long.");
        }
    }

    private static void ValidateGeneratedToken(string token, string tokenKind)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException($"Generated {tokenKind} token was empty.");
        }
    }

    private static void EnsureDifferentTokens(string accessToken, string refreshToken)
    {
        if (string.Equals(accessToken, refreshToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Access and refresh credentials must be different.");
        }
    }
}
