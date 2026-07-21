using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Worlds;

public sealed record SharedWorldHead(
    RevisionId StateRevisionId,
    RevisionId? EnvironmentRevisionId);

public enum SharedWorldReservationState
{
    Active,
    Uncertain
}

public sealed record SharedWorldReservation(
    WorldId WorldId,
    Guid SessionId,
    long Generation,
    ExternalIdentityRef Holder,
    string InstallationId,
    SharedWorldHead StartingHead,
    SharedWorldReservationState State,
    DateTimeOffset AcquiredAt,
    DateTimeOffset LastHeartbeatAt,
    DateTimeOffset? BecameUncertainAt);

public sealed record SharedWorldAuthorityOptions
{
    public SharedWorldAuthorityOptions(
        TimeSpan uncertaintyAfter,
        TimeSpan reclaimAfterUncertain)
    {
        if (uncertaintyAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(uncertaintyAfter));
        }

        if (reclaimAfterUncertain <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(reclaimAfterUncertain));
        }

        UncertaintyAfter = uncertaintyAfter;
        ReclaimAfterUncertain = reclaimAfterUncertain;
    }

    public TimeSpan UncertaintyAfter { get; }
    public TimeSpan ReclaimAfterUncertain { get; }

    public static SharedWorldAuthorityOptions FirstReleaseDefaults { get; } = new(
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(15));
}

public enum AcquireSharedWorldReservationStatus
{
    Acquired,
    AlreadyHeldByCaller,
    WorldBusy,
    WorldUncertain,
    HeadChanged,
    NotFoundOrUnauthorized
}

public sealed record AcquireSharedWorldReservationResult(
    AcquireSharedWorldReservationStatus Status,
    SharedWorldReservation? Reservation,
    SharedWorldHead? CurrentHead);

public enum SharedWorldHeartbeatStatus
{
    Accepted,
    ReservationMismatch
}

public enum ReclaimSharedWorldReservationStatus
{
    Reclaimed,
    GracePeriodRequired,
    ReservationMismatch,
    NotFoundOrUnauthorized
}

public sealed record ReclaimSharedWorldReservationResult(
    ReclaimSharedWorldReservationStatus Status,
    long? InvalidatedGeneration,
    SharedWorldHead? CurrentHead);

public sealed record CommitSharedWorldCommand(
    WorldId WorldId,
    Guid SessionId,
    long Generation,
    string InstallationId,
    SharedWorldHead ExpectedHead,
    RevisionId CandidateStateRevisionId,
    RevisionId? CandidateEnvironmentRevisionId);

public enum CommitSharedWorldStatus
{
    Committed,
    Unchanged,
    HeadChanged,
    ReservationMismatch,
    InvalidCandidate
}

public sealed record CommitSharedWorldResult(
    CommitSharedWorldStatus Status,
    SharedWorldHead CurrentHead,
    SharedWorldHead? CandidateHead);

/// <summary>
/// Transactional BE-4 World authority boundary. Implementations must keep reservation generation,
/// expected-head comparison, candidate validation, head advancement, and successful reservation
/// resolution atomic where the operation requires it.
///
/// Active membership authorizes acquisition/reclaim. Once a generation is validly acquired, that
/// generation is the completion authority for heartbeat/commit, including while membership is
/// RevocationPending. Authentication credential expiry does not itself release this authority.
/// </summary>
public interface ISharedWorldAuthorityStore
{
    Task<AcquireSharedWorldReservationResult> AcquireAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        string installationId,
        SharedWorldHead expectedHead,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default);

    Task<SharedWorldReservation?> GetReservationAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default);

    Task<SharedWorldHeartbeatStatus> HeartbeatAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        string installationId,
        Guid sessionId,
        long generation,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default);

    Task<ReclaimSharedWorldReservationResult> ReclaimAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        Guid expectedSessionId,
        long expectedGeneration,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default);

    Task<CommitSharedWorldResult> CommitAsync(
        ExternalIdentityRef caller,
        CommitSharedWorldCommand command,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default);

    Task<bool> HasUnresolvedWritableResponsibilityAsync(
        WorldId worldId,
        ExternalIdentityRef identity,
        CancellationToken cancellationToken = default);
}
