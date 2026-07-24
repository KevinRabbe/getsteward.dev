using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.Tests;

public sealed class SharedWorldHostPresenceServiceTests
{
    [Fact]
    public async Task PublishStoresPresenceOnlyForExactActiveReservation()
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var caller = Identity("76561198000000001");
        var reservation = Reservation(caller.Subject, "device-a");
        var authorityStore = new AuthorityStore { Reservation = reservation };
        var presenceStore = new PresenceStore();
        var service = CreateService(authorityStore, presenceStore, () => now);

        var status = await service.PublishAsync(
            caller,
            "device-a",
            reservation.WorldId,
            reservation.SessionId,
            reservation.Generation,
            SharedWorldHostPresenceState.Ready,
            "203.0.113.10",
            34197,
            "join-token");

        Assert.Equal(PublishSharedWorldHostPresenceStatus.Published, status);
        Assert.NotNull(presenceStore.Presence);
        var stored = presenceStore.Presence!;
        Assert.Equal(reservation.WorldId, stored.WorldId);
        Assert.Equal(reservation.SessionId, stored.SessionId);
        Assert.Equal(reservation.Generation, stored.Generation);
        Assert.Equal(caller.Subject, stored.Holder);
        Assert.Equal("device-a", stored.InstallationId);
        Assert.Equal(SharedWorldHostPresenceState.Ready, stored.State);
        Assert.Equal("203.0.113.10", stored.Address);
        Assert.Equal(34197, stored.Port);
        Assert.Equal("join-token", stored.JoinToken);
        Assert.Equal(now, stored.UpdatedAt);
    }

    [Theory]
    [InlineData("wrong-device", false, false)]
    [InlineData("device-a", true, false)]
    [InlineData("device-a", false, true)]
    public async Task PublishRejectsReservationMismatch(
        string installationId,
        bool changeSession,
        bool changeGeneration)
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var caller = Identity("76561198000000001");
        var reservation = Reservation(caller.Subject, "device-a");
        var authorityStore = new AuthorityStore { Reservation = reservation };
        var presenceStore = new PresenceStore();
        var service = CreateService(authorityStore, presenceStore, () => now);

        var sessionId = changeSession ? Guid.NewGuid() : reservation.SessionId;
        var generation = changeGeneration ? reservation.Generation + 1 : reservation.Generation;
        var status = await service.PublishAsync(
            caller,
            installationId,
            reservation.WorldId,
            sessionId,
            generation,
            SharedWorldHostPresenceState.Starting,
            address: null,
            port: null,
            joinToken: null);

        Assert.Equal(PublishSharedWorldHostPresenceStatus.ReservationMismatch, status);
        Assert.Null(presenceStore.Presence);
    }

    [Fact]
    public async Task GetVisibleHidesExpiredPresence()
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var caller = Identity("76561198000000001");
        var reservation = Reservation(caller.Subject, "device-a");
        var authorityStore = new AuthorityStore { Reservation = reservation };
        var presenceStore = new PresenceStore
        {
            Presence = Presence(reservation, now)
        };
        var service = CreateService(
            authorityStore,
            presenceStore,
            () => now,
            new SharedWorldHostPresenceOptions(TimeSpan.FromSeconds(45)));

        Assert.NotNull(await service.GetVisibleAsync(caller, reservation.WorldId));

        now = now.AddSeconds(46);

        Assert.Null(await service.GetVisibleAsync(caller, reservation.WorldId));
    }

    [Fact]
    public async Task GetVisibleHidesPresenceFromSupersededReservation()
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var caller = Identity("76561198000000001");
        var reservation = Reservation(caller.Subject, "device-a");
        var authorityStore = new AuthorityStore { Reservation = reservation };
        var presenceStore = new PresenceStore
        {
            Presence = Presence(reservation with { Generation = reservation.Generation + 1 }, now)
        };
        var service = CreateService(authorityStore, presenceStore, () => now);

        Assert.Null(await service.GetVisibleAsync(caller, reservation.WorldId));
    }

    [Fact]
    public async Task GetVisibleRequiresActiveReservation()
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var caller = Identity("76561198000000001");
        var reservation = Reservation(caller.Subject, "device-a") with
        {
            State = SharedWorldReservationState.Uncertain,
            BecameUncertainAt = now
        };
        var authorityStore = new AuthorityStore { Reservation = reservation };
        var presenceStore = new PresenceStore
        {
            Presence = Presence(reservation, now)
        };
        var service = CreateService(authorityStore, presenceStore, () => now);

        Assert.Null(await service.GetVisibleAsync(caller, reservation.WorldId));
    }

    [Fact]
    public async Task ClearDeletesOnlyMatchingCallerAndReservation()
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var caller = Identity("76561198000000001");
        var reservation = Reservation(caller.Subject, "device-a");
        var authorityStore = new AuthorityStore { Reservation = reservation };
        var presenceStore = new PresenceStore
        {
            Presence = Presence(reservation, now)
        };
        var service = CreateService(authorityStore, presenceStore, () => now);

        Assert.False(await service.ClearAsync(
            Identity("76561198000000002"),
            reservation.WorldId,
            reservation.SessionId,
            reservation.Generation));
        Assert.NotNull(presenceStore.Presence);

        Assert.True(await service.ClearAsync(
            caller,
            reservation.WorldId,
            reservation.SessionId,
            reservation.Generation));
        Assert.Null(presenceStore.Presence);
    }

    [Fact]
    public async Task ReadyPresenceRequiresConnectionAddress()
    {
        var caller = Identity("76561198000000001");
        var reservation = Reservation(caller.Subject, "device-a");
        var service = CreateService(
            new AuthorityStore { Reservation = reservation },
            new PresenceStore(),
            () => DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ArgumentException>(() => service.PublishAsync(
            caller,
            "device-a",
            reservation.WorldId,
            reservation.SessionId,
            reservation.Generation,
            SharedWorldHostPresenceState.Ready,
            address: null,
            port: 34197,
            joinToken: null));
    }

    [Fact]
    public async Task UndefinedPresenceStateIsRejectedBeforePersistence()
    {
        var caller = Identity("76561198000000001");
        var reservation = Reservation(caller.Subject, "device-a");
        var presenceStore = new PresenceStore();
        var service = CreateService(
            new AuthorityStore { Reservation = reservation },
            presenceStore,
            () => DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.PublishAsync(
            caller,
            "device-a",
            reservation.WorldId,
            reservation.SessionId,
            reservation.Generation,
            (SharedWorldHostPresenceState)999,
            address: null,
            port: null,
            joinToken: null));

        Assert.Null(presenceStore.Presence);
    }

    private static SharedWorldHostPresenceService CreateService(
        ISharedWorldAuthorityStore authorityStore,
        ISharedWorldHostPresenceStore presenceStore,
        Func<DateTimeOffset> clock,
        SharedWorldHostPresenceOptions? options = null)
        => new(
            new SharedWorldAuthorityService(authorityStore, clock),
            presenceStore,
            clock,
            options);

    private static VerifiedExternalIdentity Identity(string steamId)
        => new(new ExternalIdentityRef("steam", steamId), $"Steam {steamId}");

    private static SharedWorldReservation Reservation(
        ExternalIdentityRef holder,
        string installationId)
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        return new SharedWorldReservation(
            WorldId.New(),
            Guid.NewGuid(),
            7,
            holder,
            installationId,
            new SharedWorldHead(RevisionId.New(), RevisionId.New()),
            SharedWorldReservationState.Active,
            now,
            now,
            BecameUncertainAt: null);
    }

    private static SharedWorldHostPresence Presence(
        SharedWorldReservation reservation,
        DateTimeOffset updatedAt)
        => new(
            reservation.WorldId,
            reservation.SessionId,
            reservation.Generation,
            reservation.Holder,
            reservation.InstallationId,
            SharedWorldHostPresenceState.Ready,
            "203.0.113.10",
            34197,
            "join-token",
            updatedAt);

    private sealed class PresenceStore : ISharedWorldHostPresenceStore
    {
        public SharedWorldHostPresence? Presence { get; set; }

        public Task UpsertAsync(
            SharedWorldHostPresence presence,
            CancellationToken cancellationToken = default)
        {
            Presence = presence;
            return Task.CompletedTask;
        }

        public Task<SharedWorldHostPresence?> GetAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Presence?.WorldId == worldId ? Presence : null);

        public Task<bool> DeleteAsync(
            WorldId worldId,
            ExternalIdentityRef holder,
            Guid sessionId,
            long generation,
            CancellationToken cancellationToken = default)
        {
            var matches = Presence is not null &&
                          Presence.WorldId == worldId &&
                          Presence.Holder == holder &&
                          Presence.SessionId == sessionId &&
                          Presence.Generation == generation;
            if (matches)
            {
                Presence = null;
            }

            return Task.FromResult(matches);
        }
    }

    private sealed class AuthorityStore : ISharedWorldAuthorityStore
    {
        public SharedWorldReservation? Reservation { get; init; }

        public Task<SharedWorldReservation?> GetReservationAsync(
            ExternalIdentityRef caller,
            WorldId worldId,
            DateTimeOffset serverNow,
            SharedWorldAuthorityOptions options,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Reservation?.WorldId == worldId ? Reservation : null);

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
