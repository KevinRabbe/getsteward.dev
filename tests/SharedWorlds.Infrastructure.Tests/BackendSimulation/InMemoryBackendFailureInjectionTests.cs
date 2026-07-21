using System.Text;
using SharedWorlds.Infrastructure.BackendSimulation;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.BackendSimulation;

public sealed class InMemoryBackendFailureInjectionTests
{
    [Fact]
    public void FailureBeforeCommitLeavesPreviousHeadAndNoRecordedOperationResult()
    {
        var fixture = new Fixture();
        var reservation = fixture.Acquire();
        fixture.Publish("S1", "candidate-one");
        fixture.Simulation.FailNext(SimulatedBackendFailurePoint.BeforeCommitTransaction);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            fixture.Simulation.CommitCandidateIdempotentWithFailureInjection(
                "steam-a",
                "commit-1",
                "world-1",
                reservation.Generation,
                "S0",
                "S1"));

        Assert.Contains(
            SimulatedBackendFailurePoint.BeforeCommitTransaction.ToString(),
            exception.Message);
        Assert.Equal("S0", fixture.Simulation.Authority.GetWorld("world-1", "steam-a")?.CurrentStateRevisionId);
        Assert.Null(fixture.Simulation.GetCommitCandidateOperationResult("steam-a", "commit-1"));
        Assert.NotNull(fixture.Simulation.Authority.DownloadRevision("world-1", "steam-a", "S1"));
    }

    [Fact]
    public void ResponseLossAfterDurableCommitIsRecoveredByLookupAndRetry()
    {
        var fixture = new Fixture();
        var reservation = fixture.Acquire();
        fixture.Publish("S1", "candidate-one");
        fixture.Simulation.FailNext(SimulatedBackendFailurePoint.AfterDurableCommitBeforeResponse);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            fixture.Simulation.CommitCandidateIdempotentWithFailureInjection(
                "steam-a",
                "commit-1",
                "world-1",
                reservation.Generation,
                "S0",
                "S1"));

        var recorded = Assert.IsType<CommitCandidateResult>(
            fixture.Simulation.GetCommitCandidateOperationResult("steam-a", "commit-1"));
        var retry = fixture.Simulation.CommitCandidateIdempotent(
            "steam-a",
            "commit-1",
            "world-1",
            reservation.Generation,
            "S0",
            "S1");
        var retryResult = Assert.IsType<CommitCandidateResult>(retry.Result);

        Assert.Contains(
            SimulatedBackendFailurePoint.AfterDurableCommitBeforeResponse.ToString(),
            exception.Message);
        Assert.Equal("S1", fixture.Simulation.Authority.GetWorld("world-1", "steam-a")?.CurrentStateRevisionId);
        Assert.Equal(CommitCandidateStatus.Committed, recorded.Status);
        Assert.Equal(IdempotencyExecutionStatus.Replayed, retry.Status);
        Assert.Equal(recorded, retryResult);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Simulation = new InMemoryBackendContractSimulation(
                () => new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
            Simulation.CreateWorld(
                "world-1",
                "test-adapter",
                "S0",
                Encoding.UTF8.GetBytes("initial-state"),
                "steam-a");
        }

        public InMemoryBackendContractSimulation Simulation { get; }

        public SimulatedReservationSnapshot Acquire()
        {
            var acquired = Simulation.Authority.AcquireReservation(
                "world-1",
                "steam-a",
                "pc-a",
                "S0");
            return Assert.IsType<SimulatedReservationSnapshot>(acquired.Reservation);
        }

        public void Publish(string revisionId, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var hash = InMemorySharedWorldBackend.ComputeSha256(bytes);
            var transferId = $"transfer-{revisionId}";

            Assert.Equal(
                StartTransferStatus.Started,
                Simulation.StartTransfer(
                    transferId,
                    "world-1",
                    "steam-a",
                    revisionId,
                    bytes.LongLength,
                    hash).Status);
            Assert.Equal(
                UploadTransferPartStatus.Accepted,
                Simulation.UploadTransferPart(transferId, "steam-a", 0, bytes));
            Assert.Equal(
                FinalizeTransferStatus.Finalized,
                Simulation.FinalizeTransfer(transferId, "steam-a").Status);
        }
    }
}
