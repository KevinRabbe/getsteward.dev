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
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Compatibility shape for schema-v1 lobby implementations that predate persistent authority
    /// generations. They intentionally surface generation zero so generation-bound peer exchange
    /// refuses to send World bytes rather than treating the legacy lobby as current authority.
    /// Parameter casing intentionally matches the former positional record contract so existing
    /// named-argument callers remain source-compatible.
    /// </summary>
    public PeerWorldLobbySnapshot(
        WorldId WorldId,
        UserIdentity Owner,
        bool OwnerConfirmed,
        UserIdentity? RequestedHost,
        RevisionId? LastCommittedRevision,
        DateTimeOffset UpdatedAt)
        : this(
            WorldId,
            Owner,
            OwnerConfirmed,
            AuthorityGeneration: 0,
            RequestedHost,
            LastCommittedRevision,
            UpdatedAt)
    {
    }
}

/// <summary>
/// Narrow platform boundary required by peer-hosted Steward sessions. A Steam implementation can map
/// this contract onto one Steam lobby and its owner-transfer primitive without making Core depend on
/// Steamworks or a permanent Steward backend.
///
/// Platform ownership changes are not sufficient by themselves to establish writable World authority.
/// Implementations report whether the observed owner matches Steward's explicitly confirmed authority;
/// an automatic platform owner change must therefore surface as recovery-pending until Steward verifies
/// a usable World revision and deliberately confirms the replacement host.
///
/// AuthorityGeneration is the live lobby's claim about persistent WorldPeerAuthority.Generation.
/// Generation-aware peer exchange code requires it to be non-zero and exact. Legacy/schema-v1 lobby
/// implementations intentionally surface zero so they fail closed rather than being mistaken for
/// generation-fenced authority.
///
/// Implementations must fail closed when the expected owner no longer owns the platform lobby during
/// a mutation. Durable World bytes and revision publication are intentionally outside this boundary.
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
