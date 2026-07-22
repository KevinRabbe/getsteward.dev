using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Authority;

public interface ISharedWorldAuthorityStore
{
    Task<AcquireSharedWorldReservationResult> AcquireAsync(
        ExternalIdentityRef caller,
        AcquireSharedWorldReservationCommand command,
        DateTimeOffset serverNow,
        TimeSpan uncertaintyAfter,
        CancellationToken cancellationToken = default);

    Task<SharedWorldReservation?> LoadReservationAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        DateTimeOffset serverNow,
        TimeSpan uncertaintyAfter,
        CancellationToken cancellationToken = default);

    Task<SharedWorldHeartbeatStatus> HeartbeatAsync(
        ExternalIdentityRef caller,
        SharedWorldHeartbeatCommand command,
        DateTimeOffset serverNow,
        TimeSpan uncertaintyAfter,
        CancellationToken cancellationToken = default);

    Task<ReclaimSharedWorldReservationResult> ReclaimAsync(
        ExternalIdentityRef caller,
        ReclaimSharedWorldReservationCommand command,
        DateTimeOffset serverNow,
        TimeSpan uncertaintyAfter,
        TimeSpan reclaimGrace,
        CancellationToken cancellationToken = default);

    Task<CommitSharedWorldCandidateResult> CommitAsync(
        ExternalIdentityRef caller,
        CommitSharedWorldCandidateCommand command,
        DateTimeOffset serverNow,
        TimeSpan uncertaintyAfter,
        CancellationToken cancellationToken = default);

    Task<ReleaseSharedWorldReservationStatus> ReleaseAsync(
        ExternalIdentityRef caller,
        ReleaseSharedWorldReservationCommand command,
        DateTimeOffset serverNow,
        TimeSpan uncertaintyAfter,
        CancellationToken cancellationToken = default);
}
