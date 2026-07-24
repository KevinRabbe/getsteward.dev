using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.Tests;

public sealed class SharedWorldHostPresenceOrderingTests
{
    [Fact]
    public async Task VisibilityFailsClosedWhenReservationChangesDuringPresenceRead()
    {
        var caller = new VerifiedExternalIdentity(
            new ExternalIdentityRef("steam", "76561198000000001"),
            "Member");
        var worldId = WorldId.New();
        var oldReservation = Reservation(worldId, caller.Subject, Guid.NewGuid(), 4, "device-a");
        var newReservation = Reservation(worldId, caller.Subject, Guid.NewGuid(), 5, "device-b");
        var authorityStore = new MutableAuthorityStore { Reservation = oldReservation };
        var presenceStore = new ReclaimingPresenceStore(
            new SharedWorldHostPresence(
                worldId,
                oldReservation.SessionId,
                oldReservation.Generation,
                oldReservation.Holder,
                oldReservation.InstallationId,
                SharedWorldHostPresenceState.Ready,
                "203.0.113.20",
                34197,
                null,
                DateTimeOffset.UtcNow),
            () => authorityStore.Reservation = newReservation);
        var service = new SharedWorldHostPresenceService(
            new SharedWorldAuthorityService(authorityStore, () => DateTimeOffset.UtcNow),
            presenceStore,
            () => DateTimeOffset.UtcNow);

        Assert.Null(await service.GetVisibleAsync(caller, worldId));
        Assert.Equal(1, presenceStore.GetCount);
        Assert.Equal(1, authorityStore.GetCount);
        Assert.Equal(newReservation, authorityStore.Reservation);
    }

    private static SharedWorldReservation Reservation(
        WorldId worldId,
        ExternalIdentityRef holder,
        Guid sessionId,
        long generation,
        string installationId)
    {
        var now = DateTimeOffset.UtcNow;
        return new SharedWorldReservation(
            worldId,
            sessionId,
            generation,
            holder,
            installationId,
            new SharedWorldHead(RevisionId.New(), RevisionId.New()),
            SharedWorldReservationState.Active,
            now,
            now,
            BecameUncertainAt: null);
    }

    private sealed class ReclaimingPresenceStore : ISharedWorldHostPresenceStore
    {
        private readonly SharedWorldHostPresence _presence;
        private readonly Action _afterRead;

        public ReclaimingPresenceStore(SharedWorldHostPresence presence, Action afterRead)
        {
            _presence = presence;
            _afterRead = afterRead;
        }

        public int GetCount { get; private set; }

        public Task<bool> TryUpsertAsync(
            SharedWorldHostPresence presence,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SharedWorldHostPresence?> GetAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            GetCount++;
            _afterRead();
            return Task.FromResult<SharedWorldHostPresence?>(
                _presence.WorldId == worldId ? _presence : null);
        }

        public Task<bool> DeleteAsync(
            WorldId worldId,
            ExternalIdentityRef holder,
            string installationId,
            Guid sessionId,
            long generation,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class MutableAuthorityStore : ISharedWorldAuthorityStore
    {
        public SharedWorldReservation? Reservation { get; set; }
        public int GetCount { get; private set; }

        public Task<SharedWorldReservation?> GetReservationAsync(
            ExternalIdentityRef caller,
            WorldId worldId,
            DateTimeOffset serverNow,
            SharedWorldAuthorityOptions options,
            CancellationToken cancellationToken = default)
        {
            GetCount++;
            return Task.FromResult(
                Reservation?.WorldId == worldId ? Reservation : null);
        }

        public Task<AcquireSharedWorldReservationResult> AcquireAsync(
            ExternalIdentityRef caller,
            WorldId worldId,
            string installationId,
            SharedWorldHead expectedHead,
            DateTimeOffset serverNow,
            SharedWorldAuthorityOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SharedWorldHeartbeatStatus> HeartbeatAsync(
            ExternalIdentityRef caller,
            WorldId worldId,
            string installationId,
            Guid sessionId,
            long generation,
            DateTimeOffset serverNow,
            SharedWorldAuthorityOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ReclaimSharedWorldReservationResult> ReclaimAsync(
            ExternalIdentityRef caller,
            WorldId worldId,
            Guid expectedSessionId,
            long expectedGeneration,
            DateTimeOffset serverNow,
            SharedWorldAuthorityOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CommitSharedWorldResult> CommitAsync(
            ExternalIdentityRef caller,
            CommitSharedWorldCommand command,
            DateTimeOffset serverNow,
            SharedWorldAuthorityOptions options,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> HasUnresolvedWritableResponsibilityAsync(
            WorldId worldId,
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
