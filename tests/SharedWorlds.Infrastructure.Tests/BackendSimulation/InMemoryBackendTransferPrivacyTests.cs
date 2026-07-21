using System.Text;
using SharedWorlds.Infrastructure.BackendSimulation;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.BackendSimulation;

public sealed class InMemoryBackendTransferPrivacyTests
{
    [Fact]
    public void OtherWorldMemberCannotDistinguishExistingTransferFromUnknownTransfer()
    {
        var simulation = CreateSimulation();
        var bytes = Encoding.UTF8.GetBytes("candidate");
        var hash = InMemorySharedWorldBackend.ComputeSha256(bytes);
        simulation.StartTransfer(
            "private-transfer",
            "world-1",
            "steam-a",
            "S1",
            bytes.LongLength,
            hash);

        Assert.Null(simulation.GetTransfer("private-transfer", "steam-b"));
        Assert.Null(simulation.GetTransfer("unknown-transfer", "steam-b"));

        Assert.Equal(
            UploadTransferPartStatus.TransferNotFound,
            simulation.UploadTransferPart("private-transfer", "steam-b", 0, bytes));
        Assert.Equal(
            UploadTransferPartStatus.TransferNotFound,
            simulation.UploadTransferPart("unknown-transfer", "steam-b", 0, bytes));

        Assert.Equal(
            FinalizeTransferStatus.TransferNotFound,
            simulation.FinalizeTransfer("private-transfer", "steam-b").Status);
        Assert.Equal(
            FinalizeTransferStatus.TransferNotFound,
            simulation.FinalizeTransfer("unknown-transfer", "steam-b").Status);

        Assert.Equal(
            AbandonTransferStatus.TransferNotFound,
            simulation.AbandonTransfer("private-transfer", "steam-b"));
        Assert.Equal(
            AbandonTransferStatus.TransferNotFound,
            simulation.AbandonTransfer("unknown-transfer", "steam-b"));

        // The owner's transfer is still intact after the privacy probes.
        Assert.Equal(
            UploadTransferPartStatus.Accepted,
            simulation.UploadTransferPart("private-transfer", "steam-a", 0, bytes));
        Assert.Equal(
            FinalizeTransferStatus.Finalized,
            simulation.FinalizeTransfer("private-transfer", "steam-a").Status);
    }

    [Fact]
    public void DifferentMembersMayUseSameOpaqueTransferIdWithoutCrossCallerCollision()
    {
        var simulation = CreateSimulation();
        var aBytes = Encoding.UTF8.GetBytes("candidate-a");
        var bBytes = Encoding.UTF8.GetBytes("candidate-b");

        var aStart = simulation.StartTransfer(
            "same-id",
            "world-1",
            "steam-a",
            "S1-a",
            aBytes.LongLength,
            InMemorySharedWorldBackend.ComputeSha256(aBytes));
        var bStart = simulation.StartTransfer(
            "same-id",
            "world-1",
            "steam-b",
            "S1-b",
            bBytes.LongLength,
            InMemorySharedWorldBackend.ComputeSha256(bBytes));

        Assert.Equal(StartTransferStatus.Started, aStart.Status);
        Assert.Equal(StartTransferStatus.Started, bStart.Status);

        var aSnapshot = Assert.IsType<SimulatedTransferSnapshot>(
            simulation.GetTransfer("same-id", "steam-a"));
        var bSnapshot = Assert.IsType<SimulatedTransferSnapshot>(
            simulation.GetTransfer("same-id", "steam-b"));

        Assert.Equal("S1-a", aSnapshot.RevisionId);
        Assert.Equal("S1-b", bSnapshot.RevisionId);

        Assert.Equal(
            UploadTransferPartStatus.Accepted,
            simulation.UploadTransferPart("same-id", "steam-a", 0, aBytes));
        Assert.Equal(
            UploadTransferPartStatus.Accepted,
            simulation.UploadTransferPart("same-id", "steam-b", 0, bBytes));
        Assert.Equal(
            FinalizeTransferStatus.Finalized,
            simulation.FinalizeTransfer("same-id", "steam-a").Status);
        Assert.Equal(
            FinalizeTransferStatus.Finalized,
            simulation.FinalizeTransfer("same-id", "steam-b").Status);
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
}
