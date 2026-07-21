using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Sessions;

namespace SharedWorlds.Core.Abstractions;

/// <summary>
/// Live coordination boundary. The Core understands only session/writer authority; transport and
/// provider details remain outside it.
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

    Task RequestHandoffAsync(WorldId worldId, UserIdentity requestedHost, CancellationToken cancellationToken = default);
    Task CompleteHandoffAsync(WorldId worldId, UserIdentity newHost, RevisionId committedRevision, CancellationToken cancellationToken = default);
    Task ReleaseHostAsync(WorldId worldId, UserIdentity user, CancellationToken cancellationToken = default);
}
