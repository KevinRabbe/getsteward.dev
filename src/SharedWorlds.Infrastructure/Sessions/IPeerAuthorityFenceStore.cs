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
