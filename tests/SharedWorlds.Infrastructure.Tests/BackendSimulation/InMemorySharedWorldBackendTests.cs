using System.Text;
using SharedWorlds.Infrastructure.BackendSimulation;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.BackendSimulation;

public sealed class InMemorySharedWorldBackendTests
{
    [Fact]
    public void CompetingWriterCannotAcquireSameWorld()
    {
        var fixture = new Fixture();

        var first = fixture.Backend.AcquireReservation("world-1", "steam-a", "pc-a", "S0");
        var second = fixture.Backend.AcquireReservation("world-1", "steam-b", "pc-b", "S0");

        Assert.Equal(AcquireReservationStatus.Acquired, first.Status);
        Assert.Equal(AcquireReservationStatus.AlreadyReserved, second.Status);
        Assert.Equal(first.Reservation, second.Reservation);
    }

    [Fact]
    public void StaleExpectedHeadCannotAdvanceCanonicalState()
    {
        var fixture = new Fixture();
        var reservation = fixture.AcquireA();
        fixture.Publish("steam-a", "S1", "candidate-one");

        var result = fixture.Backend.CommitCandidate(
            "world-1",
            "steam-a",
            reservation.Generation,
            expectedHeadRevisionId: "stale-head",
            candidateRevisionId: "S1");

        Assert.Equal(CommitCandidateStatus.HeadChanged, result.Status);
        Assert.Equal("S0", result.CurrentHeadRevisionId);
        Assert.NotNull(fixture.Backend.DownloadRevision("world-1", "steam-a", "S1"));
        Assert.Equal("S0", fixture.Backend.GetWorld("world-1", "steam-a")?.CurrentStateRevisionId);
    }

    [Fact]
    public void WrongGenerationHeartbeatDoesNotChangeReservation()
    {
        var fixture = new Fixture();
        var reservation = fixture.AcquireA();

        var result = fixture.Backend.Heartbeat(
            "world-1",
            "steam-a",
            "pc-a",
            reservation.Generation + 1);

        Assert.Equal(HeartbeatStatus.ReservationMismatch, result);
        Assert.Equal(reservation, fixture.Backend.GetReservation("world-1", "steam-a"));
    }

    [Fact]
    public void MissedHeartbeatCreatesUncertaintyAndNeverAvailability()
    {
        var fixture = new Fixture();
        fixture.AcquireA();

        fixture.Clock.Advance(InMemorySharedWorldBackend.UncertaintyAfter);

        var reservation = fixture.Backend.GetReservation("world-1", "steam-a");
        var competing = fixture.Backend.AcquireReservation("world-1", "steam-b", "pc-b", "S0");

        Assert.NotNull(reservation);
        Assert.Equal(SimulatedReservationState.Uncertain, reservation.State);
        Assert.Equal(AcquireReservationStatus.WorldUncertain, competing.Status);
    }

    [Fact]
    public void SameValidGenerationReconnectsDuringUncertainty()
    {
        var fixture = new Fixture();
        var original = fixture.AcquireA();
        fixture.Clock.Advance(InMemorySharedWorldBackend.UncertaintyAfter);
        Assert.Equal(
            SimulatedReservationState.Uncertain,
            fixture.Backend.GetReservation("world-1", "steam-a")?.State);

        var heartbeat = fixture.Backend.Heartbeat(
            "world-1",
            "steam-a",
            "pc-a",
            original.Generation);
        var resumed = fixture.Backend.GetReservation("world-1", "steam-a");

        Assert.Equal(HeartbeatStatus.Accepted, heartbeat);
        Assert.NotNull(resumed);
        Assert.Equal(SimulatedReservationState.Active, resumed.State);
        Assert.Null(resumed.BecameUncertainAt);
        Assert.Equal(original.Generation, resumed.Generation);
    }

    [Fact]
    public void ReclaimAfterGraceInvalidatesOldGenerationBeforeNewAcquire()
    {
        var fixture = new Fixture();
        var oldReservation = fixture.AcquireA();
        fixture.Publish("steam-a", "S1", "old-session-candidate");

        fixture.Clock.Advance(InMemorySharedWorldBackend.UncertaintyAfter);
        Assert.Equal(
            SimulatedReservationState.Uncertain,
            fixture.Backend.GetReservation("world-1", "steam-b")?.State);
        fixture.Clock.Advance(InMemorySharedWorldBackend.ReclaimAfterUncertain);

        var reclaim = fixture.Backend.ReclaimUncertainReservation("world-1", "steam-b");
        var replacement = fixture.Backend.AcquireReservation("world-1", "steam-b", "pc-b", "S0");
        var lateCommit = fixture.Backend.CommitCandidate(
            "world-1",
            "steam-a",
            oldReservation.Generation,
            "S0",
            "S1");

        Assert.Equal(ReclaimReservationStatus.Reclaimed, reclaim.Status);
        Assert.Equal(oldReservation.Generation, reclaim.InvalidatedGeneration);
        Assert.Equal(AcquireReservationStatus.Acquired, replacement.Status);
        Assert.NotNull(replacement.Reservation);
        Assert.True(replacement.Reservation.Generation > oldReservation.Generation);
        Assert.Equal(CommitCandidateStatus.ReservationMismatch, lateCommit.Status);
        Assert.Equal("S0", fixture.Backend.GetWorld("world-1", "steam-a")?.CurrentStateRevisionId);
        Assert.NotNull(fixture.Backend.DownloadRevision("world-1", "steam-a", "S1"));
    }

    [Fact]
    public void CandidateMustMatchDeclaredSizeAndHashBeforePublication()
    {
        var fixture = new Fixture();
        var bytes = Bytes("candidate");

        var result = fixture.Backend.PublishCandidate(
            "world-1",
            "steam-a",
            "S1",
            bytes,
            expectedByteSize: bytes.LongLength + 1,
            expectedSha256: InMemorySharedWorldBackend.ComputeSha256(bytes));

        Assert.Equal(PublishCandidateStatus.InvalidCandidate, result.Status);
        Assert.Null(fixture.Backend.DownloadRevision("world-1", "steam-a", "S1"));
    }

    [Fact]
    public void VerifiedCandidateCommitsOnlyForExpectedHeadAndActiveGeneration()
    {
        var fixture = new Fixture();
        var reservation = fixture.AcquireA();
        fixture.Publish("steam-a", "S1", "committed-state");

        var result = fixture.Backend.CommitCandidate(
            "world-1",
            "steam-a",
            reservation.Generation,
            "S0",
            "S1");

        Assert.Equal(CommitCandidateStatus.Committed, result.Status);
        Assert.Equal("S1", result.CurrentHeadRevisionId);
        Assert.Equal("S1", fixture.Backend.GetWorld("world-1", "steam-b")?.CurrentStateRevisionId);
    }

    [Fact]
    public void UnauthorizedCallerCannotDiscoverWorldOrRevision()
    {
        var fixture = new Fixture();

        Assert.Null(fixture.Backend.GetWorld("world-1", "steam-outsider"));
        Assert.Null(fixture.Backend.GetReservation("world-1", "steam-outsider"));
        Assert.Null(fixture.Backend.DownloadRevision("world-1", "steam-outsider", "S0"));

        var acquire = fixture.Backend.AcquireReservation(
            "world-1",
            "steam-outsider",
            "pc-x",
            "S0");

        Assert.Equal(AcquireReservationStatus.Unauthorized, acquire.Status);
        Assert.Null(acquire.ObservedHeadRevisionId);
    }

    private static byte[] Bytes(string content) => Encoding.UTF8.GetBytes(content);

    private sealed class Fixture
    {
        public Fixture()
        {
            Clock = new MutableClock(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
            Backend = new InMemorySharedWorldBackend(() => Clock.UtcNow);
            Backend.CreateWorld(
                "world-1",
                "test-adapter",
                "S0",
                Bytes("initial-state"),
                "steam-a",
                new[] { "steam-b" });
        }

        public MutableClock Clock { get; }
        public InMemorySharedWorldBackend Backend { get; }

        public SimulatedReservationSnapshot AcquireA()
        {
            var result = Backend.AcquireReservation("world-1", "steam-a", "pc-a", "S0");
            Assert.Equal(AcquireReservationStatus.Acquired, result.Status);
            return Assert.IsType<SimulatedReservationSnapshot>(result.Reservation);
        }

        public void Publish(string identity, string revisionId, string content)
        {
            var bytes = Bytes(content);
            var result = Backend.PublishCandidate(
                "world-1",
                identity,
                revisionId,
                bytes,
                bytes.LongLength,
                InMemorySharedWorldBackend.ComputeSha256(bytes));

            Assert.Equal(PublishCandidateStatus.Published, result.Status);
        }
    }

    private sealed class MutableClock
    {
        public MutableClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; private set; }

        public void Advance(TimeSpan amount)
        {
            UtcNow += amount;
        }
    }
}
