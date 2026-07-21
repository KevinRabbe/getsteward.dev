using System.Text;
using SharedWorlds.Infrastructure.BackendSimulation;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.BackendSimulation;

public sealed class InMemoryBackendRevisionScopeTests
{
    [Fact]
    public void SameOpaqueRevisionIdsRemainIndependentAcrossWorlds()
    {
        var backend = new InMemorySharedWorldBackend(
            () => new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
        backend.CreateWorld(
            "world-a",
            "test-adapter",
            "S0",
            Bytes("initial-a"),
            "steam-a");
        backend.CreateWorld(
            "world-b",
            "test-adapter",
            "S0",
            Bytes("initial-b"),
            "steam-b");

        Assert.Equal(Bytes("initial-a"), backend.DownloadRevision("world-a", "steam-a", "S0"));
        Assert.Equal(Bytes("initial-b"), backend.DownloadRevision("world-b", "steam-b", "S0"));

        var aCandidate = Bytes("candidate-a");
        var bCandidate = Bytes("candidate-b");
        Assert.Equal(
            PublishCandidateStatus.Published,
            backend.PublishCandidate(
                "world-a",
                "steam-a",
                "S1",
                aCandidate,
                aCandidate.LongLength,
                InMemorySharedWorldBackend.ComputeSha256(aCandidate)).Status);
        Assert.Equal(
            PublishCandidateStatus.Published,
            backend.PublishCandidate(
                "world-b",
                "steam-b",
                "S1",
                bCandidate,
                bCandidate.LongLength,
                InMemorySharedWorldBackend.ComputeSha256(bCandidate)).Status);

        var aReservation = Assert.IsType<SimulatedReservationSnapshot>(
            backend.AcquireReservation("world-a", "steam-a", "pc-a", "S0").Reservation);
        var bReservation = Assert.IsType<SimulatedReservationSnapshot>(
            backend.AcquireReservation("world-b", "steam-b", "pc-b", "S0").Reservation);

        Assert.Equal(
            CommitCandidateStatus.Committed,
            backend.CommitCandidate(
                "world-a",
                "steam-a",
                aReservation.Generation,
                "S0",
                "S1").Status);
        Assert.Equal(
            CommitCandidateStatus.Committed,
            backend.CommitCandidate(
                "world-b",
                "steam-b",
                bReservation.Generation,
                "S0",
                "S1").Status);

        Assert.Equal(Bytes("candidate-a"), backend.DownloadRevision("world-a", "steam-a", "S1"));
        Assert.Equal(Bytes("candidate-b"), backend.DownloadRevision("world-b", "steam-b", "S1"));
        Assert.Equal("S1", backend.GetWorld("world-a", "steam-a")?.CurrentStateRevisionId);
        Assert.Equal("S1", backend.GetWorld("world-b", "steam-b")?.CurrentStateRevisionId);
    }

    [Fact]
    public void RevisionFromOtherWorldIsNotVisibleThroughAuthorizedWorldLookup()
    {
        var backend = new InMemorySharedWorldBackend(
            () => new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
        backend.CreateWorld(
            "world-a",
            "test-adapter",
            "S0-a",
            Bytes("initial-a"),
            "steam-shared");
        backend.CreateWorld(
            "world-b",
            "test-adapter",
            "S0-b",
            Bytes("initial-b"),
            "steam-shared");

        var privateToA = Bytes("candidate-a");
        backend.PublishCandidate(
            "world-a",
            "steam-shared",
            "candidate-only-in-a",
            privateToA,
            privateToA.LongLength,
            InMemorySharedWorldBackend.ComputeSha256(privateToA));

        Assert.Equal(
            privateToA,
            backend.DownloadRevision("world-a", "steam-shared", "candidate-only-in-a"));
        Assert.Null(
            backend.DownloadRevision("world-b", "steam-shared", "candidate-only-in-a"));
    }

    private static byte[] Bytes(string value)
        => Encoding.UTF8.GetBytes(value);
}
