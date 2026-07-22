using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Authority;

public enum SharedWorldReservationState
{
    Active,
    Uncertain
}

public enum SharedWorldSessionMode
{
    Local,
    Hosted
}

public sealed record SharedWorldReservation(
    WorldId WorldId,
    Guid SessionId,
    long Generation,
    ExternalIdentityRef Holder,
    string InstallationId,
    SharedWorldSessionMode Mode,
    RevisionId StartingStateRevisionId,
    RevisionId? StartingEnvironmentRevisionId,
    SharedWorldReservationState State,
    DateTimeOffset AcquiredAt,
    DateTimeOffset LastHeartbeatAt,
    DateTimeOffset? BecameUncertainAt);

public enum AcquireSharedWorldReservationStatus
{
    Acquired,
    AlreadyHeldByCaller,
    WorldBusy,
    WorldUncertain,
    HeadChanged,
    NotFoundOrUnauthorized
}

public sealed record AcquireSharedWorldReservationCommand(
    WorldId WorldId,
    string InstallationId,
    SharedWorldSessionMode Mode,
    RevisionId ExpectedStateRevisionId,
    RevisionId? ExpectedEnvironmentRevisionId);

public sealed record AcquireSharedWorldReservationResult(
    AcquireSharedWorldReservationStatus Status,
    SharedWorldReservation? Reservation,
    RevisionId? ObservedStateRevisionId,
    RevisionId? ObservedEnvironmentRevisionId);

public enum SharedWorldHeartbeatStatus
{
    Accepted,
    ReservationMismatch,
    NotFoundOrUnauthorized
}

public sealed record SharedWorldHeartbeatCommand(
    WorldId WorldId,
    string InstallationId,
    long Generation);

public enum ReclaimSharedWorldReservationStatus
{
    Reclaimed,
    GracePeriodRequired,
    SessionStillActive,
    ReservationMismatch,
    HeadChanged,
    NotFoundOrUnauthorized
}

public sealed record ReclaimSharedWorldReservationCommand(
    WorldId WorldId,
    long ExpectedGeneration,
    RevisionId ExpectedStateRevisionId,
    RevisionId? ExpectedEnvironmentRevisionId);

public sealed record ReclaimSharedWorldReservationResult(
    ReclaimSharedWorldReservationStatus Status,
    long? InvalidatedGeneration,
    RevisionId? ObservedStateRevisionId,
    RevisionId? ObservedEnvironmentRevisionId);

public enum CommitSharedWorldCandidateStatus
{
    Committed,
    Unchanged,
    HeadChanged,
    ReservationMismatch,
    InvalidCandidate,
    NotFoundOrUnauthorized
}

public sealed record CommitSharedWorldCandidateCommand(
    WorldId WorldId,
    string InstallationId,
    long Generation,
    RevisionId ExpectedStateRevisionId,
    RevisionId? ExpectedEnvironmentRevisionId,
    RevisionId CandidateStateRevisionId);

public sealed record CommitSharedWorldCandidateResult(
    CommitSharedWorldCandidateStatus Status,
    RevisionId? CurrentStateRevisionId,
    RevisionId? CurrentEnvironmentRevisionId,
    RevisionId? CandidateStateRevisionId);

public enum ReleaseSharedWorldReservationStatus
{
    Released,
    ReservationMismatch,
    NotFoundOrUnauthorized
}

public sealed record ReleaseSharedWorldReservationCommand(
    WorldId WorldId,
    string InstallationId,
    long Generation);
