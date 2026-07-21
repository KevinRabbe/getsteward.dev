using System.Text;
using SharedWorlds.Infrastructure.BackendSimulation;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.BackendSimulation;

public sealed class InMemoryBackendTransferBoundsTests
{
    [Fact]
    public void UniquePartThatWouldExceedDeclaredPackageSizeIsRejectedImmediately()
    {
        var simulation = CreateSimulation();
        var expected = Bytes("abcdef");
        var hash = InMemorySharedWorldBackend.ComputeSha256(expected);
        simulation.StartTransfer(
            "transfer-s1",
            "world-1",
            "steam-a",
            "S1",
            expected.LongLength,
            hash);

        Assert.Equal(
            UploadTransferPartStatus.Accepted,
            simulation.UploadTransferPart("transfer-s1", "steam-a", 0, expected.AsSpan(0, 4)));

        var overrun = simulation.UploadTransferPart(
            "transfer-s1",
            "steam-a",
            1,
            Bytes("xyz"));
        var snapshot = Assert.IsType<SimulatedTransferSnapshot>(
            simulation.GetTransfer("transfer-s1", "steam-a"));

        Assert.Equal(UploadTransferPartStatus.InvalidPart, overrun);
        Assert.Equal(new[] { 0 }, snapshot.CompletedPartNumbers);
        Assert.Equal(
            FinalizeTransferStatus.IncompleteOrInvalid,
            simulation.FinalizeTransfer("transfer-s1", "steam-a").Status);
    }

    [Fact]
    public void IdenticalPartReplayDoesNotConsumeDeclaredByteBudgetTwice()
    {
        var simulation = CreateSimulation();
        var expected = Bytes("abcdef");
        var hash = InMemorySharedWorldBackend.ComputeSha256(expected);
        simulation.StartTransfer(
            "transfer-s1",
            "world-1",
            "steam-a",
            "S1",
            expected.LongLength,
            hash);

        var first = simulation.UploadTransferPart(
            "transfer-s1",
            "steam-a",
            0,
            expected.AsSpan(0, 3));
        var replay = simulation.UploadTransferPart(
            "transfer-s1",
            "steam-a",
            0,
            expected.AsSpan(0, 3));
        var remainder = simulation.UploadTransferPart(
            "transfer-s1",
            "steam-a",
            1,
            expected.AsSpan(3));
        var finalized = simulation.FinalizeTransfer("transfer-s1", "steam-a");

        Assert.Equal(UploadTransferPartStatus.Accepted, first);
        Assert.Equal(UploadTransferPartStatus.AlreadyAccepted, replay);
        Assert.Equal(UploadTransferPartStatus.Accepted, remainder);
        Assert.Equal(FinalizeTransferStatus.Finalized, finalized.Status);
        Assert.Equal(
            expected,
            Assert.IsType<byte[]>(simulation.Authority.DownloadRevision("world-1", "steam-a", "S1")));
    }

    [Fact]
    public void SinglePartLargerThanEntireDeclaredPackageIsRejectedWithoutRetention()
    {
        var simulation = CreateSimulation();
        var expected = Bytes("abc");
        var hash = InMemorySharedWorldBackend.ComputeSha256(expected);
        simulation.StartTransfer(
            "transfer-s1",
            "world-1",
            "steam-a",
            "S1",
            expected.LongLength,
            hash);

        var result = simulation.UploadTransferPart(
            "transfer-s1",
            "steam-a",
            0,
            Bytes("oversized"));
        var snapshot = Assert.IsType<SimulatedTransferSnapshot>(
            simulation.GetTransfer("transfer-s1", "steam-a"));

        Assert.Equal(UploadTransferPartStatus.InvalidPart, result);
        Assert.Empty(snapshot.CompletedPartNumbers);
    }

    private static InMemoryBackendContractSimulation CreateSimulation()
    {
        var simulation = new InMemoryBackendContractSimulation(
            () => new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
        simulation.CreateWorld(
            "world-1",
            "test-adapter",
            "S0",
            Bytes("initial-state"),
            "steam-a");
        return simulation;
    }

    private static byte[] Bytes(string value)
        => Encoding.UTF8.GetBytes(value);
}
