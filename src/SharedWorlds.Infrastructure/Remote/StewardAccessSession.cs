namespace SharedWorlds.Infrastructure.Remote;

public interface IStewardAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

public sealed class StewardSessionExpiredException : InvalidOperationException
{
    public StewardSessionExpiredException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// In-memory Steward credential owner for the desktop/background runtime. It refreshes only when the
/// access credential approaches expiry and serializes refresh so concurrent background operations do
/// not rotate the same refresh credential more than once.
/// </summary>
public sealed class StewardAccessSession : IStewardAccessTokenProvider, IDisposable
{
    private static readonly TimeSpan DefaultRefreshSkew = TimeSpan.FromMinutes(2);

    private readonly StewardSessionClient _client;
    private readonly string _installationId;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _refreshSkew;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private StewardRemoteSessionTokens? _tokens;
    private bool _disposed;

    public StewardAccessSession(
        StewardSessionClient client,
        string installationId,
        StewardRemoteSessionTokens initialTokens,
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? refreshSkew = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        ArgumentNullException.ThrowIfNull(initialTokens);

        var effectiveSkew = refreshSkew ?? DefaultRefreshSkew;
        if (effectiveSkew < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(refreshSkew));
        }

        ValidateTokens(initialTokens);
        _client = client;
        _installationId = installationId;
        _tokens = initialTokens;
        _utcNow = utcNow ?? static () => DateTimeOffset.UtcNow;
        _refreshSkew = effectiveSkew;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var current = _tokens ?? throw SessionEnded();
        if (IsAccessUsable(current, _utcNow()))
        {
            return current.AccessToken;
        }

        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            current = _tokens ?? throw SessionEnded();
            var now = _utcNow();
            if (IsAccessUsable(current, now))
            {
                return current.AccessToken;
            }

            if (current.RefreshExpiresAt <= now)
            {
                _tokens = null;
                throw SessionEnded();
            }

            var result = await _client.RefreshAsync(
                current.RefreshToken,
                _installationId,
                cancellationToken);
            if (result.Status != RemoteSessionRefreshStatus.Refreshed || result.Tokens is null)
            {
                _tokens = null;
                throw SessionEnded();
            }

            ValidateTokens(result.Tokens);
            _tokens = result.Tokens;
            return result.Tokens.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<bool> RevokeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var current = _tokens;
            if (current is null)
            {
                return false;
            }

            var revoked = await _client.RevokeAsync(
                current.RefreshToken,
                _installationId,
                cancellationToken);
            _tokens = null;
            return revoked;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tokens = null;
        _refreshGate.Dispose();
    }

    private bool IsAccessUsable(StewardRemoteSessionTokens tokens, DateTimeOffset now)
        => tokens.AccessExpiresAt - now > _refreshSkew;

    private static void ValidateTokens(StewardRemoteSessionTokens tokens)
    {
        ValidateCredential(tokens.AccessToken, nameof(tokens.AccessToken));
        ValidateCredential(tokens.RefreshToken, nameof(tokens.RefreshToken));
        if (tokens.AccessExpiresAt == default)
        {
            throw new ArgumentException("Access expiry is required.", nameof(tokens));
        }

        if (tokens.RefreshExpiresAt == default)
        {
            throw new ArgumentException("Refresh expiry is required.", nameof(tokens));
        }
    }

    private static void ValidateCredential(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Credential must be non-empty and contain no whitespace.", parameterName);
        }
    }

    private static StewardSessionExpiredException SessionEnded()
        => new("The Steward session is no longer refreshable and must be authenticated again.");

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
