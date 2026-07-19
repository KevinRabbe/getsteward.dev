using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Sessions;

namespace SharedWorlds.Core.Abstractions;

/// <summary>
/// Live coordination boundary. A Steam lobby is one possible implementation,
/// but the Core only understands sessions and host ownership.
/// </summary>
public interface IWorldSessionCoordinator
{
    Task<WorldSession> GetSessionAsync(WorldId worldId, CancellationToken cancellationToken = default);
    Task<WorldSession> AcquireHostAsync(WorldId worldId, UserIdentity user, CancellationToken cancellationToken = default);
    Task RequestHandoffAsync(WorldId worldId, UserIdentity requestedHost, CancellationToken cancellationToken = default);
    Task CompleteHandoffAsync(WorldId worldId, UserIdentity newHost, RevisionId committedRevision, CancellationToken cancellationToken = default);
    Task ReleaseHostAsync(WorldId worldId, UserIdentity user, CancellationToken cancellationToken = default);
}
