using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Remote;

/// <summary>
/// Client-side seam intended to sit directly below a remote IWorldStorage.OpenRevisionAsync.
/// The caller supplies a current Steward access token; only a verified cached package stream is
/// returned to the existing lifecycle/materialization path.
/// </summary>
public sealed class StewardVerifiedPackageSource
{
    private readonly StewardPackageDownloadClient _authorizationClient;
    private readonly VerifiedPackageCache _cache;

    public StewardVerifiedPackageSource(
        StewardPackageDownloadClient authorizationClient,
        VerifiedPackageCache cache)
    {
        ArgumentNullException.ThrowIfNull(authorizationClient);
        ArgumentNullException.ThrowIfNull(cache);
        _authorizationClient = authorizationClient;
        _cache = cache;
    }

    public async Task<Stream> OpenStateRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var authorization = await _authorizationClient.AuthorizeDownloadAsync(
            worldId,
            revisionId,
            RemotePackageKind.State,
            accessToken,
            cancellationToken);
        return await _cache.OpenVerifiedReadAsync(authorization, cancellationToken);
    }

    public async Task<Stream> OpenEnvironmentPackageAsync(
        WorldId worldId,
        RevisionId revisionId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var authorization = await _authorizationClient.AuthorizeDownloadAsync(
            worldId,
            revisionId,
            RemotePackageKind.Environment,
            accessToken,
            cancellationToken);
        return await _cache.OpenVerifiedReadAsync(authorization, cancellationToken);
    }
}
