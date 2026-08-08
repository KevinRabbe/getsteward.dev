using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Ephemeral platform lobby state for one active shared World. The platform lobby is not durable
/// World storage: it identifies only the current host and an optional graceful-handoff target.
/// </summary>
public sealed record PeerWorldLobbySnapshot(
    WorldId WorldId,
    UserIdentity Owner,
    UserIdentity? RequestedHost,
    RevisionId? LastCommittedRevision,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Narrow platform boundary required by peer-hosted Steward sessions. A Steam implementation can map
/// this contract onto one Steam lobby and its owner-transfer primitive without making Core depend on
/// Steamworks or a permanent Steward backend.
///
/// Implementations must fail closed if <paramref name="expectedOwner"/> no longer owns the platform
/// lobby when a mutation is attempted. Durable World bytes and revision publication are intentionally
/// outside this boundary.
/// </summary>
public interface IPeerWorldLobby
{
    Task<PeerWorldLobbySnapshot?> GetAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the active lobby when none exists, or returns the already-observed lobby. The caller
    /// decides whether an existing owner is acceptable; this prevents the transport from inventing
    /// writable World authority.
    /// </summary>
    Task<PeerWorldLobbySnapshot> CreateOrGetAsync(
        WorldId worldId,
        UserIdentity proposedOwner,
        CancellationToken cancellationToken = default);

    Task<PeerWorldLobbySnapshot> RequestHandoffAsync(
        WorldId worldId,
        UserIdentity expectedOwner,
        UserIdentity requestedHost,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transfers platform-lobby ownership only after Steward has committed the final outgoing-host
    /// revision supplied here. The committed revision is handoff evidence, not mutable lobby storage
    /// for the World itself.
    /// </summary>
    Task<PeerWorldLobbySnapshot> TransferOwnershipAsync(
        WorldId worldId,
        UserIdentity expectedOwner,
        UserIdentity newOwner,
        RevisionId committedRevision,
        CancellationToken cancellationToken = default);

    Task LeaveAsync(
        WorldId worldId,
        UserIdentity expectedOwner,
        CancellationToken cancellationToken = default);
}
