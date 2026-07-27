using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Desktop;

/// <summary>
/// Keeps the Core Join lifecycle unchanged while adding Desktop-only, best-effort lobby presence around
/// an automatically observed client session. The inner service remains the authority for Join ordering.
/// </summary>
internal sealed class CoordinatedWorldJoinService
{
    private readonly WorldJoinService _inner;
    private readonly StewardWorldPlayerPresenceClient _presence;

    public CoordinatedWorldJoinService(
        WorldJoinService inner,
        StewardWorldPlayerPresenceClient presence)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(presence);
        _inner = inner;
        _presence = presence;
    }

    public Task JoinAsync(
        WorldId worldId,
        IGameAdapter adapter,
        GameInstallation installation,
        HostConnection host,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return _inner.JoinAsync(
            worldId,
            new CoordinatedJoinGameAdapter(adapter, _presence, worldId),
            installation,
            host,
            cancellationToken);
    }
}
