using System.Text;
using SharedWorlds.Infrastructure.BackendSimulation;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.BackendSimulation;

public sealed class InMemoryBackendRecoveryIdempotencyTests
{
    [Fact]
    public void ReclaimReplayReturnsOriginalInvalidatedGeneration()
    {
        var clock = new MutableClock(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
        var simulation = CreateSimulation(() => clock.UtcNow);
        var acquired = simulation.Authority.AcquireReservation("world-1", "steam-a", "pc-a", "S0");
        var original = Assert.IsType<SimulatedReservationSnapshot>(acquired.Reservation);
        clock.Advance(
            InMemorySharedWorldBackend.UncertaintyAfter +
            InMemorySharedWorldBackend.ReclaimAfterUncertain +
            TimeSpan.FromSeconds(1));

        var first = simulation.ReclaimUncertainReservationIdempotent(
            "steam-b",
            "reclaim-1",
            "world-1");
        var replay = simulation.ReclaimUncertainReservationIdempotent(
            "steam-b",
            "reclaim-1",
            "world-1");
        var firstResult = Assert.IsType<ReclaimReservationResult>(first.Result);
        var replayResult = Assert.IsType<ReclaimReservationResult>(replay.Result);

        Assert.Equal(IdempotencyExecutionStatus.Executed, first.Status);
        Assert.Equal(ReclaimReservationStatus.Reclaimed, firstResult.Status);
        Assert.Equal(original.Generation, firstResult.InvalidatedGeneration);
        Assert.Equal(IdempotencyExecutionStatus.Replayed, replay.Status);
        Assert.Equal(firstResult, replayResult);
        Assert.Equal(
            firstResult,
            Assert.IsType<ReclaimReservationResult>(
                simulation.GetReclaimReservationOperationResult("steam-b", "reclaim-1")));
    }

    [Fact]
    public void LastSafeReplayReturnsOriginalCompletionAndPreservesCandidate()
    {
        var simulation = CreateSimulation(
            () => new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
        var acquired = simulation.Authority.AcquireReservation("world-1", "steam-a", "pc-a", "S0");
        var reservation = Assert.IsType<SimulatedReservationSnapshot>(acquired.Reservation);
        PublishCandidate(simulation, "S1", "uncommitted-progress");

        var first = simulation.ContinueFromLastSafeStateIdempotent(
            "steam-a",
            "recover-1",
            "world-1",
            reservation.Generation,
            "S1");
        var replay = simulation.ContinueFromLastSafeStateIdempotent(
            "steam-a",
            "recover-1",
            "world-1",
            reservation.Generation,
            "S1");
        var firstResult = Assert.IsType<ContinueFromLastSafeStateOperationResult>(first.Result);
        var replayResult = Assert.IsType<ContinueFromLastSafeStateOperationResult>(replay.Result);

        Assert.Equal(IdempotencyExecutionStatus.Executed, first.Status);
        Assert.Equal(ContinueFromLastSafeStateStatus.Completed, firstResult.Status);
        Assert.Equal(IdempotencyExecutionStatus.Replayed, replay.Status);
        Assert.Equal(firstResult, replayResult);
        Assert.Null(simulation.Authority.GetReservation("world-1", "steam-a"));
        Assert.Equal("S0", simulation.Authority.GetWorld("world-1", "steam-a")?.CurrentStateRevisionId);
        Assert.True(simulation.IsCandidateAbandoned("world-1", "S1"));
        Assert.NotNull(simulation.Authority.DownloadRevision("world-1", "steam-a", "S1"));
    }

    [Fact]
    public void LastSafeIdempotencyKeyCannotBeReusedForDifferentCandidate()
    {
        var simulation = CreateSimulation(
            () => new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
        var acquired = simulation.Authority.AcquireReservation("world-1", "steam-a", "pc-a", "S0");
        var reservation = Assert.IsType<SimulatedReservationSnapshot>(acquired.Reservation);
        PublishCandidate(simulation, "S1", "candidate-one");
        PublishCandidate(simulation, "S2", "candidate-two");

        var first = simulation.ContinueFromLastSafeStateIdempotent(
            "steam-a",
            "recover-1",
            "world-1",
            reservation.Generation,
            "S1");
        var conflict = simulation.ContinueFromLastSafeStateIdempotent(
            "steam-a",
            "recover-1",
            "world-1",
            reservation.Generation,
            "S2");

        Assert.Equal(IdempotencyExecutionStatus.Executed, first.Status);
        Assert.Equal(IdempotencyExecutionStatus.KeyReuseConflict, conflict.Status);
        Assert.Null(conflict.Result);
        Assert.True(simulation.IsCandidateAbandoned("world-1", "S1"));
        Assert.False(simulation.IsCandidateAbandoned("world-1", "S2"));
    }

    private static InMemoryBackendContractSimulation CreateSimulation(Func<DateTimeOffset> utcNow)
    {
        var simulation = new InMemoryBackendContractSimulation(utcNow);
        simulation.CreateWorld(
            "world-1",
            "test-adapter",
            "S0",
            Encoding.UTF8.GetBytes("initial-state"),
            "steam-a",
            new[] { "steam-b" });
        return simulation;
    }

    private static void PublishCandidate(
        InMemoryBackendContractSimulation simulation,
        string revisionId,
        string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = InMemorySharedWorldBackend.ComputeSha256(bytes);
        var transferId = $"transfer-{revisionId}";

        Assert.Equal(
            StartTransferStatus.Started,
            simulation.StartTransfer(
                transferId,
                "world-1",
                "steam-a",
                revisionId,
                bytes.LongLength,
                hash).Status);
        Assert.Equal(
            UploadTransferPartStatus.Accepted,
            simulation.UploadTransferPart(transferId, "steam-a", 0, bytes));
        Assert.Equal(
            FinalizeTransferStatus.Finalized,
            simulation.FinalizeTransfer(transferId, "steam-a").Status);
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
