using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Sessions;

namespace SharedWorlds.Core.Abstractions;

/// <summary>
/// Live coordination boundary. Core owns the timing of writer/session state; transport, network-address
/// discovery, and provider details remain outside it.
/// </summary>
public interface IWorldSessionCoordinator
{
    Task<WorldSession> GetSessionAsync(WorldId worldId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires writable authority for the World. Implementations with remote/ambiguous transport
    /// outcomes must resolve idempotency/status before returning. Throwing from this method means
    /// the implementation has established that this caller did not acquire writable authority.
    /// </summary>
    Task<WorldSession> AcquireHostAsync(
        WorldId worldId,
        UserIdentity user,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records non-authoritative evidence that the current writer has begun launching a managed host.
    /// Implementations must treat publication failure as a Join-availability failure, not as loss of
    /// writable World authority or proof that gameplay did not start.
    /// </summary>
    Task MarkHostStartingAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Records non-authoritative connection material for an already-running managed host. The endpoint
    /// contains only adapter-owned game data; a remote coordinator may derive the visible address itself.
    /// </summary>
    Task MarkHostReadyAsync(
        WorldId worldId,
        ManagedHostEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops advertising the current managed host. This does not release writable authority; normal
    /// capture/commit/recovery still completes before ReleaseHostAsync resolves the writer reservation.
    /// </summary>
    Task EndHostPresenceAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    Task RequestHandoffAsync(WorldId worldId, UserIdentity requestedHost, CancellationToken cancellationToken = default);
    Task CompleteHandoffAsync(WorldId worldId, UserIdentity newHost, RevisionId committedRevision, CancellationToken cancellationToken = default);
    Task ReleaseHostAsync(WorldId worldId, UserIdentity user, CancellationToken cancellationToken = default);
}
