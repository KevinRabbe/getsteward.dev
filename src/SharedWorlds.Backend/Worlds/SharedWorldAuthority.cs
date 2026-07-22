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

public sealed record StewardIdempotencyKey
{
    public StewardIdempotencyKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 128 || value.Any(character => character is < '!' or > '~'))
        {
            throw new ArgumentException(
                "Idempotency key must contain at most 128 visible ASCII characters.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }
}

public enum IdempotentMutationStatus
{
    Executed,
    Replayed,
    KeyConflict
}

public sealed record IdempotentMutationResult<T>(
    IdempotentMutationStatus Status,
    T? Result);

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
///
/// Idempotent mutation variants must persist the exact completed domain result in the same authority
/// transaction as the mutation. Reusing the same key with different logical input returns
/// KeyConflict; retrying the same key/input returns the original result rather than re-executing it.
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

    Task<IdempotentMutationResult<AcquireSharedWorldReservationResult>> AcquireIdempotentAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        string installationId,
        SharedWorldHead expectedHead,
        StewardIdempotencyKey idempotencyKey,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This authority store does not support durable idempotency.");

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

    Task<IdempotentMutationResult<ReclaimSharedWorldReservationResult>> ReclaimIdempotentAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        Guid expectedSessionId,
        long expectedGeneration,
        StewardIdempotencyKey idempotencyKey,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This authority store does not support durable idempotency.");

    Task<CommitSharedWorldResult> CommitAsync(
        ExternalIdentityRef caller,
        CommitSharedWorldCommand command,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default);

    Task<IdempotentMutationResult<CommitSharedWorldResult>> CommitIdempotentAsync(
        ExternalIdentityRef caller,
        CommitSharedWorldCommand command,
        StewardIdempotencyKey idempotencyKey,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This authority store does not support durable idempotency.");

    Task<bool> HasUnresolvedWritableResponsibilityAsync(
        WorldId worldId,
        ExternalIdentityRef identity,
        CancellationToken cancellationToken = default);
}
