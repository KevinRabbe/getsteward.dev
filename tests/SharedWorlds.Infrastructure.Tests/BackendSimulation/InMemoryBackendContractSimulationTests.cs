using System.Text;
using SharedWorlds.Infrastructure.BackendSimulation;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.BackendSimulation;

public sealed class InMemoryBackendContractSimulationTests
{
    [Fact]
    public void InterruptedTransferResumesFromRecordedPartsAndPublishesOnlyAfterVerification()
    {
        var fixture = new Fixture();
        var bytes = Bytes("abcdef");
        var hash = InMemorySharedWorldBackend.ComputeSha256(bytes);

        var started = fixture.Simulation.StartTransfer(
            "transfer-s1",
            "world-1",
            "steam-a",
            "S1",
            bytes.LongLength,
            hash);
        var firstPart = fixture.Simulation.UploadTransferPart(
            "transfer-s1",
            "steam-a",
            0,
            bytes.AsSpan(0, 3));

        var resumed = fixture.Simulation.StartTransfer(
            "transfer-s1",
            "world-1",
            "steam-a",
            "S1",
            bytes.LongLength,
            hash);

        Assert.Equal(StartTransferStatus.Started, started.Status);
        Assert.Equal(UploadTransferPartStatus.Accepted, firstPart);
        Assert.Equal(StartTransferStatus.Resumed, resumed.Status);
        Assert.Equal(new[] { 0 }, resumed.Transfer?.CompletedPartNumbers);
        Assert.Null(fixture.Simulation.Authority.DownloadRevision("world-1", "steam-a", "S1"));

        Assert.Equal(
            UploadTransferPartStatus.Accepted,
            fixture.Simulation.UploadTransferPart("transfer-s1", "steam-a", 1, bytes.AsSpan(3)));

        var finalized = fixture.Simulation.FinalizeTransfer("transfer-s1", "steam-a");

        Assert.Equal(FinalizeTransferStatus.Finalized, finalized.Status);
        Assert.Equal(PublishCandidateStatus.Published, finalized.Publication?.Status);
        Assert.Equal(bytes, fixture.Simulation.Authority.DownloadRevision("world-1", "steam-a", "S1"));
    }

    [Fact]
    public void IncompleteTransferCannotPublishCandidate()
    {
        var fixture = new Fixture();
        var bytes = Bytes("complete-package");
        var hash = InMemorySharedWorldBackend.ComputeSha256(bytes);

        fixture.Simulation.StartTransfer(
            "transfer-s1",
            "world-1",
            "steam-a",
            "S1",
            bytes.LongLength,
            hash);
        fixture.Simulation.UploadTransferPart(
            "transfer-s1",
            "steam-a",
            0,
            bytes.AsSpan(0, 4));

        var result = fixture.Simulation.FinalizeTransfer("transfer-s1", "steam-a");

        Assert.Equal(FinalizeTransferStatus.IncompleteOrInvalid, result.Status);
        Assert.Null(fixture.Simulation.Authority.DownloadRevision("world-1", "steam-a", "S1"));
    }

    [Fact]
    public void SameTransferPartIsRetrySafeButDifferentBytesConflict()
    {
        var fixture = new Fixture();
        var bytes = Bytes("part-data");
        var hash = InMemorySharedWorldBackend.ComputeSha256(bytes);
        fixture.Simulation.StartTransfer(
            "transfer-s1",
            "world-1",
            "steam-a",
            "S1",
            bytes.LongLength,
            hash);

        var first = fixture.Simulation.UploadTransferPart("transfer-s1", "steam-a", 0, bytes);
        var replay = fixture.Simulation.UploadTransferPart("transfer-s1", "steam-a", 0, bytes);
        var conflict = fixture.Simulation.UploadTransferPart(
            "transfer-s1",
            "steam-a",
            0,
            Bytes("different"));

        Assert.Equal(UploadTransferPartStatus.Accepted, first);
        Assert.Equal(UploadTransferPartStatus.AlreadyAccepted, replay);
        Assert.Equal(UploadTransferPartStatus.PartConflict, conflict);
    }

    [Fact]
    public void PackageCeilingRejectsOversizedTransferBeforeBytesAreAccepted()
    {
        var fixture = new Fixture();

        var result = fixture.Simulation.StartTransfer(
            "too-large",
            "world-1",
            "steam-a",
            "S1",
            InMemoryBackendContractSimulation.MaximumPackageBytes + 1,
            "UNUSED");

        Assert.Equal(StartTransferStatus.InvalidRequest, result.Status);
        Assert.Null(result.Transfer);
    }

    [Fact]
    public void IdempotentAcquireReplaysOriginalGenerationAndRejectsKeyReuseForDifferentInput()
    {
        var fixture = new Fixture();

        var first = fixture.Simulation.AcquireReservationIdempotent(
            "steam-a",
            "acquire-1",
            "world-1",
            "pc-a",
            "S0");
        var replay = fixture.Simulation.AcquireReservationIdempotent(
            "steam-a",
            "acquire-1",
            "world-1",
            "pc-a",
            "S0");
        var conflict = fixture.Simulation.AcquireReservationIdempotent(
            "steam-a",
            "acquire-1",
            "world-1",
            "different-device",
            "S0");

        Assert.Equal(IdempotencyExecutionStatus.Executed, first.Status);
        Assert.Equal(AcquireReservationStatus.Acquired, first.Result?.Status);
        Assert.Equal(IdempotencyExecutionStatus.Replayed, replay.Status);
        Assert.Equal(first.Result, replay.Result);
        Assert.Equal(IdempotencyExecutionStatus.KeyReuseConflict, conflict.Status);
        Assert.Null(conflict.Result);
    }

    [Fact]
    public void IdempotentCommitReturnsOriginalCommittedResultAfterHeadAlreadyAdvanced()
    {
        var fixture = new Fixture();
        var reservation = fixture.Acquire("steam-a", "pc-a", "S0");
        fixture.UploadCandidate("steam-a", "S1", "state-one");

        var first = fixture.Simulation.CommitCandidateIdempotent(
            "steam-a",
            "commit-1",
            "world-1",
            reservation.Generation,
            "S0",
            "S1");
        var replay = fixture.Simulation.CommitCandidateIdempotent(
            "steam-a",
            "commit-1",
            "world-1",
            reservation.Generation,
            "S0",
            "S1");

        Assert.Equal(IdempotencyExecutionStatus.Executed, first.Status);
        Assert.Equal(CommitCandidateStatus.Committed, first.Result?.Status);
        Assert.Equal(IdempotencyExecutionStatus.Replayed, replay.Status);
        Assert.Equal(first.Result, replay.Result);
        Assert.Equal("S1", fixture.Simulation.Authority.GetWorld("world-1", "steam-a")?.CurrentStateRevisionId);
    }

    [Fact]
    public void IdempotentTransferFinalizationReplaysOriginalResult()
    {
        var fixture = new Fixture();
        fixture.StartAndUpload("steam-a", "transfer-s1", "S1", "state-one");

        var first = fixture.Simulation.FinalizeTransferIdempotent(
            "steam-a",
            "finalize-1",
            "transfer-s1");
        var replay = fixture.Simulation.FinalizeTransferIdempotent(
            "steam-a",
            "finalize-1",
            "transfer-s1");

        Assert.Equal(IdempotencyExecutionStatus.Executed, first.Status);
        Assert.Equal(FinalizeTransferStatus.Finalized, first.Result?.Status);
        Assert.Equal(IdempotencyExecutionStatus.Replayed, replay.Status);
        Assert.Equal(first.Result, replay.Result);
    }

    [Fact]
    public void InvalidLastSafeCandidateDoesNotReleaseValidReservation()
    {
        var fixture = new Fixture();
        var reservation = fixture.Acquire("steam-a", "pc-a", "S0");

        var result = fixture.Simulation.ContinueFromLastSafeState(
            "world-1",
            "steam-a",
            reservation.Generation,
            "missing-candidate");

        Assert.Equal(ContinueFromLastSafeStateStatus.InvalidCandidate, result);
        Assert.Equal(
            reservation.Generation,
            fixture.Simulation.Authority.GetReservation("world-1", "steam-a")?.Generation);
    }

    [Fact]
    public void ContinueFromLastSafeStateReleasesAuthorityButPreservesAbandonedCandidate()
    {
        var fixture = new Fixture();
        var reservation = fixture.Acquire("steam-a", "pc-a", "S0");
        fixture.UploadCandidate("steam-a", "S1", "uncommitted-progress");

        var result = fixture.Simulation.ContinueFromLastSafeState(
            "world-1",
            "steam-a",
            reservation.Generation,
            "S1");

        Assert.Equal(ContinueFromLastSafeStateStatus.Completed, result);
        Assert.Null(fixture.Simulation.Authority.GetReservation("world-1", "steam-a"));
        Assert.Equal("S0", fixture.Simulation.Authority.GetWorld("world-1", "steam-a")?.CurrentStateRevisionId);
        Assert.True(fixture.Simulation.IsCandidateAbandoned("world-1", "S1"));
        Assert.NotNull(fixture.Simulation.Authority.DownloadRevision("world-1", "steam-a", "S1"));
    }

    [Fact]
    public void TwoClientHandoffAdvancesAtoBtoAWithoutCompetingWriter()
    {
        var fixture = new Fixture();

        var a = fixture.Acquire("steam-a", "pc-a", "S0");
        fixture.UploadCandidate("steam-a", "S1", "state-from-a");
        Assert.Equal(
            CommitCandidateStatus.Committed,
            fixture.Simulation.Authority.CommitCandidate(
                "world-1",
                "steam-a",
                a.Generation,
                "S0",
                "S1").Status);
        Assert.True(fixture.Simulation.Authority.ReleaseReservation("world-1", "steam-a", a.Generation));

        Assert.Equal(
            Bytes("state-from-a"),
            fixture.Simulation.Authority.DownloadRevision("world-1", "steam-b", "S1"));

        var b = fixture.Acquire("steam-b", "pc-b", "S1");
        var competing = fixture.Simulation.Authority.AcquireReservation(
            "world-1",
            "steam-a",
            "pc-a",
            "S1");
        Assert.Equal(AcquireReservationStatus.AlreadyReserved, competing.Status);

        fixture.UploadCandidate("steam-b", "S2", "state-from-b");
        Assert.Equal(
            CommitCandidateStatus.Committed,
            fixture.Simulation.Authority.CommitCandidate(
                "world-1",
                "steam-b",
                b.Generation,
                "S1",
                "S2").Status);
        Assert.True(fixture.Simulation.Authority.ReleaseReservation("world-1", "steam-b", b.Generation));

        Assert.Equal("S2", fixture.Simulation.Authority.GetWorld("world-1", "steam-a")?.CurrentStateRevisionId);
        Assert.Equal(
            Bytes("state-from-b"),
            fixture.Simulation.Authority.DownloadRevision("world-1", "steam-a", "S2"));
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private sealed class Fixture
    {
        public Fixture()
        {
            Clock = new MutableClock(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
            Simulation = new InMemoryBackendContractSimulation(() => Clock.UtcNow);
            Simulation.CreateWorld(
                "world-1",
                "test-adapter",
                "S0",
                Bytes("initial-state"),
                "steam-a",
                new[] { "steam-b" });
        }

        public MutableClock Clock { get; }
        public InMemoryBackendContractSimulation Simulation { get; }

        public SimulatedReservationSnapshot Acquire(string identity, string device, string expectedHead)
        {
            var result = Simulation.Authority.AcquireReservation(
                "world-1",
                identity,
                device,
                expectedHead);
            Assert.Equal(AcquireReservationStatus.Acquired, result.Status);
            return Assert.IsType<SimulatedReservationSnapshot>(result.Reservation);
        }

        public void UploadCandidate(string identity, string revisionId, string content)
        {
            var transferId = $"transfer-{revisionId}-{identity}";
            StartAndUpload(identity, transferId, revisionId, content);
            var finalized = Simulation.FinalizeTransfer(transferId, identity);
            Assert.Equal(FinalizeTransferStatus.Finalized, finalized.Status);
        }

        public void StartAndUpload(string identity, string transferId, string revisionId, string content)
        {
            var bytes = Bytes(content);
            var hash = InMemorySharedWorldBackend.ComputeSha256(bytes);
            var started = Simulation.StartTransfer(
                transferId,
                "world-1",
                identity,
                revisionId,
                bytes.LongLength,
                hash);
            Assert.Equal(StartTransferStatus.Started, started.Status);
            Assert.Equal(
                UploadTransferPartStatus.Accepted,
                Simulation.UploadTransferPart(transferId, identity, 0, bytes));
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
