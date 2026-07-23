using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlSharedRevisionRetentionStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlSharedWorldStore _worldStore = null!;
    private SharedWorldMetadataService _worlds = null!;
    private SharedRevisionMetadataService _revisions = null!;
    private PostgreSqlSharedWorldAuthorityStore _authority = null!;
    private PostgreSqlSharedPackageTransferStore _transfers = null!;
    private PostgreSqlSharedRevisionRetentionStore _retentionStore = null!;
    private VerifiedExternalIdentity _manager = null!;
    private SharedWorldMetadata _world = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("STEWARD_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "STEWARD_TEST_POSTGRES must be set for PostgreSQL integration tests.");
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
        await PostgreSqlBackendSchema.InitializeAsync(_dataSource);
        await ResetAsync();

        _worldStore = new PostgreSqlSharedWorldStore(_dataSource);
        _worlds = new SharedWorldMetadataService(_worldStore, () => Now);
        _revisions = new SharedRevisionMetadataService(_worldStore, _worldStore);
        _authority = new PostgreSqlSharedWorldAuthorityStore(_dataSource);
        _transfers = new PostgreSqlSharedPackageTransferStore(_dataSource);
        _retentionStore = new PostgreSqlSharedRevisionRetentionStore(_dataSource);
        _manager = Steam("76561198000000001");

        var created = await _worlds.CreateSharedWorldAsync(
            _manager,
            new CreateSharedWorldCommand(
                WorldId.New(),
                "factorio",
                "Retention World",
                RevisionId.New(),
                null));
        _world = Assert.IsType<SharedWorldMetadata>(created.World);
        await RecordStateAsync(_world.CurrentStateRevisionId, Hex('A'), Now);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task InitialHeadAndSuccessfulCommitsAreSequencedCanonically()
    {
        var initial = Assert.Single(await _retentionStore.ListRecentCanonicalHeadsAsync(
            _world.WorldId,
            count: 3));
        Assert.Equal(0, initial.Sequence);
        Assert.Equal(_world.CurrentStateRevisionId, initial.Head.StateRevisionId);

        var first = await CommitNewStateAsync('B', 1);
        var second = await CommitNewStateAsync('C', 2);
        var third = await CommitNewStateAsync('D', 3);

        var retained = await _retentionStore.ListRecentCanonicalHeadsAsync(
            _world.WorldId,
            count: 3);

        Assert.Collection(
            retained,
            entry =>
            {
                Assert.Equal(3, entry.Sequence);
                Assert.Equal(third, entry.Head.StateRevisionId);
            },
            entry =>
            {
                Assert.Equal(2, entry.Sequence);
                Assert.Equal(second, entry.Head.StateRevisionId);
            },
            entry =>
            {
                Assert.Equal(1, entry.Sequence);
                Assert.Equal(first, entry.Head.StateRevisionId);
            });
    }

    [Fact]
    public async Task FourthCanonicalHeadMakesOnlyOldestCanonicalStateCleanupEligible()
    {
        var initialRevision = _world.CurrentStateRevisionId;
        var first = await CommitNewStateAsync('B', 1);
        var second = await CommitNewStateAsync('C', 2);
        var third = await CommitNewStateAsync('D', 3);

        var service = new SharedRevisionRetentionService(
            _retentionStore,
            () => Now.AddMinutes(4));
        var candidates = await service.ListCleanupEligibleAsync();

        var candidate = Assert.Single(candidates);
        Assert.Equal(initialRevision, candidate.RevisionId);
        Assert.Equal(SharedPackageKind.State, candidate.Kind);
        Assert.True(candidate.WasCanonical);
        Assert.DoesNotContain(candidates, item => item.RevisionId == first);
        Assert.DoesNotContain(candidates, item => item.RevisionId == second);
        Assert.DoesNotContain(candidates, item => item.RevisionId == third);
    }

    [Fact]
    public async Task NeverCommittedCandidateWaitsForGraceAndActiveWriterPinsOwnedCandidate()
    {
        var candidate = RevisionId.New();
        await RecordStateAsync(candidate, Hex('B'), Now);

        var beforeGrace = new SharedRevisionRetentionService(
            _retentionStore,
            () => Now.AddDays(7).AddSeconds(-1));
        Assert.DoesNotContain(
            await beforeGrace.ListCleanupEligibleAsync(),
            item => item.RevisionId == candidate);

        var afterGrace = new SharedRevisionRetentionService(
            _retentionStore,
            () => Now.AddDays(7));
        Assert.Contains(
            await afterGrace.ListCleanupEligibleAsync(),
            item => item.RevisionId == candidate && !item.WasCanonical);

        var current = Assert.IsType<SharedWorldMetadata>(
            await _worldStore.LoadWorldAsync(_world.WorldId));
        var acquired = await _authority.AcquireAsync(
            _manager.Subject,
            _world.WorldId,
            "device-a",
            Head(current),
            Now.AddDays(8),
            SharedWorldAuthorityOptions.FirstReleaseDefaults);
        Assert.Equal(AcquireSharedWorldReservationStatus.Acquired, acquired.Status);

        var transfer = new SharedPackageTransferRecord(
            SharedPackageTransferId.New(),
            _world.WorldId,
            candidate,
            SharedPackageKind.State,
            _world.AdapterId,
            _manager.Subject,
            $"packages/{_world.WorldId}/state/{candidate}.package",
            $"provider-{Guid.NewGuid():N}",
            4096,
            Hex('B'),
            null,
            1024,
            4,
            Now,
            Now.AddHours(24),
            SharedPackageTransferState.Finalized,
            Now.AddMinutes(1));
        Assert.True(await _transfers.TryCreateAsync(transfer));

        var whileResponsible = new SharedRevisionRetentionService(
            _retentionStore,
            () => Now.AddDays(8));
        Assert.DoesNotContain(
            await whileResponsible.ListCleanupEligibleAsync(),
            item => item.RevisionId == candidate);
    }

    private async Task<RevisionId> CommitNewStateAsync(char hashCharacter, int minute)
    {
        var current = Assert.IsType<SharedWorldMetadata>(
            await _worldStore.LoadWorldAsync(_world.WorldId));
        var serverNow = Now.AddMinutes(minute);
        var acquired = await _authority.AcquireAsync(
            _manager.Subject,
            _world.WorldId,
            "device-a",
            Head(current),
            serverNow,
            SharedWorldAuthorityOptions.FirstReleaseDefaults);
        var reservation = Assert.IsType<SharedWorldReservation>(acquired.Reservation);

        var revision = RevisionId.New();
        await RecordStateAsync(revision, Hex(hashCharacter), serverNow);
        var committed = await _authority.CommitAsync(
            _manager.Subject,
            new CommitSharedWorldCommand(
                _world.WorldId,
                reservation.SessionId,
                reservation.Generation,
                "device-a",
                reservation.StartingHead,
                revision,
                null),
            serverNow,
            SharedWorldAuthorityOptions.FirstReleaseDefaults);
        Assert.Equal(CommitSharedWorldStatus.Committed, committed.Status);
        return revision;
    }

    private async Task RecordStateAsync(
        RevisionId revisionId,
        string sha256,
        DateTimeOffset publishedAt)
    {
        var status = await _revisions.RecordVerifiedStateRevisionAsync(
            new SharedStateRevisionMetadata(
                _world.WorldId,
                revisionId,
                _world.AdapterId,
                $"packages/{_world.WorldId}/state/{revisionId}.package",
                4096,
                sha256,
                null,
                _manager.Subject,
                publishedAt));
        Assert.True(status is RecordRevisionMetadataStatus.Recorded or RecordRevisionMetadataStatus.AlreadyRecorded);
    }

    private async Task ResetAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "TRUNCATE TABLE steward_world_reservations, steward_package_transfers, " +
            "steward_world_invitations, steward_state_revisions, steward_environment_revisions, " +
            "steward_world_members, steward_shared_worlds CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private static SharedWorldHead Head(SharedWorldMetadata world)
        => new(world.CurrentStateRevisionId, world.CurrentEnvironmentRevisionId);

    private static string Hex(char character) => new(character, 64);

    private static VerifiedExternalIdentity Steam(string id)
        => new(new ExternalIdentityRef("steam", id));
}
