using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Sessions;

public enum PeerAuthorityFenceState
{
    Active = 1,
    Observed = 2,
    Relinquishing = 3
}

/// <summary>
/// Small durable per-account fence for one shared World. This is not World storage. It prevents an
/// old local replica owned by the same Steam account from resurrecting stale writable authority after
/// that account has observed or initiated a newer authority generation.
/// </summary>
public sealed record PeerAuthorityFence(
    WorldId WorldId,
    UserIdentity Holder,
    ulong Generation,
    RevisionId StateRevisionId,
    PeerAuthorityFenceState State,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Persists the local account's highest accepted authority fence independently from local World files.
/// A Steam implementation stores this tiny record in the current user's Steam Cloud. Missing,
/// malformed, or stale fence data must fail closed for peer hosting.
/// </summary>
public interface IPeerAuthorityFenceStore
{
    Task<PeerAuthorityFence?> LoadAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        PeerAuthorityFence fence,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Narrow compare-before-write boundary used when the current peer host commits another state revision
/// without changing authority generation. Generic SaveAsync deliberately does not permit arbitrary
/// same-generation revision replacement: a stale replica must not be able to overwrite a newer fence.
///
/// Implementations must accept an exact retry when the durable fence already equals the requested next
/// Active tuple. Otherwise the current durable fence must exactly match holder/generation/expected state
/// and be Active before it may advance to nextStateRevisionId.
/// </summary>
public interface IPeerAuthorityActiveRevisionFenceStore : IPeerAuthorityFenceStore
{
    Task<PeerAuthorityFence> AdvanceActiveRevisionAsync(
        WorldId worldId,
        UserIdentity holder,
        ulong generation,
        RevisionId expectedStateRevisionId,
        RevisionId nextStateRevisionId,
        CancellationToken cancellationToken = default);
}
