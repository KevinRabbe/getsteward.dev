using System.Text;
using SharedWorlds.Infrastructure.BackendSimulation;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.BackendSimulation;

public sealed class InMemoryBackendOperationLookupTests
{
    [Fact]
    public void CompletedCommitCanBeLookedUpAfterResponseLoss()
    {
        var simulation = CreateSimulation();
        var acquired = simulation.Authority.AcquireReservation("world-1", "steam-a", "pc-a", "S0");
        var reservation = Assert.IsType<SimulatedReservationSnapshot>(acquired.Reservation);
        PublishCandidate(simulation, "S1", "state-one");

        var executed = simulation.CommitCandidateIdempotent(
            "steam-a",
            "commit-op-1",
            "world-1",
            reservation.Generation,
            "S0",
            "S1");

        var recorded = simulation.GetCommitCandidateOperationResult("steam-a", "commit-op-1");
        var unknown = simulation.GetCommitCandidateOperationResult("steam-a", "unknown-op");

        Assert.Equal(IdempotencyExecutionStatus.Executed, executed.Status);
        Assert.Equal(CommitCandidateStatus.Committed, Assert.IsType<CommitCandidateResult>(executed.Result).Status);
        Assert.Equal(CommitCandidateStatus.Committed, Assert.IsType<CommitCandidateResult>(recorded).Status);
        Assert.Null(unknown);
        Assert.Equal("S1", simulation.Authority.GetWorld("world-1", "steam-a")?.CurrentStateRevisionId);
    }

    [Fact]
    public void OperationLookupIsScopedToCallerIdentity()
    {
        var simulation = CreateSimulation();
        var executed = simulation.AcquireReservationIdempotent(
            "steam-a",
            "acquire-op-1",
            "world-1",
            "pc-a",
            "S0");

        Assert.Equal(IdempotencyExecutionStatus.Executed, executed.Status);
        Assert.NotNull(simulation.GetAcquireReservationOperationResult("steam-a", "acquire-op-1"));
        Assert.Null(simulation.GetAcquireReservationOperationResult("steam-b", "acquire-op-1"));
    }

    private static InMemoryBackendContractSimulation CreateSimulation()
    {
        var simulation = new InMemoryBackendContractSimulation(
            () => new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
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
        var transferId = $"transfer-{revisionId}";
        var hash = InMemorySharedWorldBackend.ComputeSha256(bytes);

        var start = simulation.StartTransfer(
            transferId,
            "world-1",
            "steam-a",
            revisionId,
            bytes.LongLength,
            hash);
        Assert.Equal(StartTransferStatus.Started, start.Status);
        Assert.Equal(
            UploadTransferPartStatus.Accepted,
            simulation.UploadTransferPart(transferId, "steam-a", 0, bytes));
        Assert.Equal(
            FinalizeTransferStatus.Finalized,
            simulation.FinalizeTransfer(transferId, "steam-a").Status);
    }
}
