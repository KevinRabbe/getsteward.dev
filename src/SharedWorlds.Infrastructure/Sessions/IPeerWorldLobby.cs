using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Ephemeral platform lobby state for one active shared World. The platform lobby is not durable
/// World storage: it identifies only the current host and an optional graceful-handoff target.
/// </summary>
public sealed record PeerWorldLobbySnapshot(
    WorldId WorldId,
    UserIdentity Owner,
    bool OwnerConfirmed,
    ulong AuthorityGeneration,
    UserIdentity? RequestedHost,
    RevisionId? LastCommittedRevision,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Narrow platform boundary required by peer-hosted Steward sessions. A Steam implementation can map
/// this contract onto one Steam lobby and its owner-transfer primitive without making Core depend on
/// Steamworks or a permanent Steward backend.
///
/// Every mutation is generation-fenced. The caller supplies the exact persistent World authority
/// generation it expects the live lobby to represent. Implementations must fail closed when owner or
/// generation no longer matches. A graceful ownership transfer is the only mutation allowed to move
/// the lobby generation, and it must move exactly from N to N+1.
///
/// Platform ownership changes are not sufficient by themselves to establish writable World authority.
/// Implementations report whether the observed owner matches Steward's explicitly confirmed authority;
/// an automatic platform owner change must therefore surface as recovery-pending until Steward verifies
/// a usable World revision and deliberately confirms the replacement host.
///
/// Durable World bytes and revision publication are intentionally outside this boundary.
/// </summary>
public interface IPeerWorldLobby
{
    Task<PeerWorldLobbySnapshot?> GetAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default);

    Task<PeerWorldLobbySnapshot> CreateOrGetAsync(
        WorldId worldId,
        UserIdentity proposedOwner,
        ulong authorityGeneration,
        CancellationToken cancellationToken = default);

    Task<PeerWorldLobbySnapshot> RequestHandoffAsync(
        WorldId worldId,
        UserIdentity expectedOwner,
        ulong expectedAuthorityGeneration,
        UserIdentity requestedHost,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Transfers platform-lobby ownership only after Steward has committed and replicated the final
    /// outgoing-host revision. expectedAuthorityGeneration is the current live generation and
    /// newAuthorityGeneration must equal expectedAuthorityGeneration + 1.
    /// </summary>
    Task<PeerWorldLobbySnapshot> TransferOwnershipAsync(
        WorldId worldId,
        UserIdentity expectedOwner,
        ulong expectedAuthorityGeneration,
        UserIdentity newOwner,
        ulong newAuthorityGeneration,
        RevisionId committedRevision,
        CancellationToken cancellationToken = default);

    Task LeaveAsync(
        WorldId worldId,
        UserIdentity expectedOwner,
        ulong expectedAuthorityGeneration,
        CancellationToken cancellationToken = default);
}
