using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Guarantees that a proposed peer host possesses and has verified the exact canonical World state
/// revision required to take authority. Implementations may transfer a complete portable World or
/// only missing content, but must not return until the target can load the revision and its referenced
/// environment without relying on the outgoing host.
/// </summary>
public interface IPeerWorldRevisionTransfer
{
    Task EnsureAvailableAsync(
        WorldId worldId,
        RevisionId committedStateRevision,
        UserIdentity targetHost,
        CancellationToken cancellationToken = default);
}
