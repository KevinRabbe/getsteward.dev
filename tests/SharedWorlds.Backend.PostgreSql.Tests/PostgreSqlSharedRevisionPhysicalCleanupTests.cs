using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlSharedRevisionPhysicalCleanupTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlSharedWorldStore _worldStore = null!;
    private SharedWorldMetadataService _worlds = null!;
    private SharedRevisionMetadataService _revisions = null!;
    private PostgreSqlSharedWorldAuthorityStore _authority = null!;
    private PostgreSqlSharedPackageTransferStore _transfers = null!;
    private PostgreSqlSharedRevisionRetentionStore _retention = null!;
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
        _retention = new PostgreSqlSharedRevisionRetentionStore(_dataSource);
        _manager = Steam("76561198000000001");

        var created = await _worlds.CreateSharedWorldAsync(
            _manager,
            new CreateSharedWorldCommand(
                WorldId.New(),
                "factorio",
                "Physical Cleanup World",
                RevisionId.New(),
                null));
        _world = Assert.IsType<SharedWorldMetadata>(created.World);
        await RecordStateAsync(_world.CurrentStateRevisionId, Hex('A'), Now, requiredEnvironment: null);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task OldCanonicalStateRetiresMetadataAndDurablyQueuesObjectDeletion()
    {
        var initial = _world.CurrentStateRevisionId;
        await CommitNewStateAsync('B', 1);
        await CommitNewStateAsync('C', 2);
        await CommitNewStateAsync('D', 3);

        var candidate = Assert.Single(await _retention.ListCleanupEligibleAsync(
            Now - TimeSpan.FromDays(7),
            retainedCanonicalHeadCount: 3,
            limit: 100));
        Assert.Equal(initial, candidate.RevisionId);

        var status = await _retention.TryRetireCleanupCandidateAsync(
            candidate,
            Now - TimeSpan.FromDays(7),
            retainedCanonicalHeadCount: 3,
            Now.AddMinutes(4));

        Assert.Equal(RetireSharedRevisionStatus.Retired, status);
        Assert.Null(await _worldStore.LoadStateRevisionAsync(_world.WorldId, initial));
        var current = Assert.IsType<SharedWorldMetadata>(await _worldStore.LoadWorldAsync(_world.WorldId));
        Assert.NotEqual(initial, current.CurrentStateRevisionId);

        var queued = Assert.Single(await _retention.ListPendingObjectCleanupAsync(
            DateTimeOffset.MaxValue,
            limit: 100));
        Assert.Equal($"packages/{_world.WorldId}/state/{initial}.package", queued.ObjectKey);
        Assert.Equal(0, queued.AttemptCount);

        Assert.True(await _retention.TryRecordObjectCleanupFailureAsync(
            queued.ObjectKey,
            Now.AddMinutes(5)));
        Assert.Empty(await _retention.ListPendingObjectCleanupAsync(
            Now.AddMinutes(5).AddTicks(-1),
            limit: 100));
        var retry = Assert.Single(await _retention.ListPendingObjectCleanupAsync(
            Now.AddMinutes(5),
            limit: 100));
        Assert.Equal(1, retry.AttemptCount);
        Assert.Equal(Now.AddMinutes(5), retry.LastAttemptAt);

        Assert.True(await _retention.TryCompleteObjectCleanupAsync(retry.ObjectKey));
        Assert.Empty(await _retention.ListPendingObjectCleanupAsync(DateTimeOffset.MaxValue, 100));
    }

    [Fact]
    public async Task StaleCleanupSelectionCannotRetireRevisionThatBecameCurrentAgain()
    {
        var initial = _world.CurrentStateRevisionId;
        await CommitNewStateAsync('B', 1);
        await CommitNewStateAsync('C', 2);
        await CommitNewStateAsync('D', 3);

        var staleCandidate = Assert.Single(await _retention.ListCleanupEligibleAsync(
            Now - TimeSpan.FromDays(7),
            retainedCanonicalHeadCount: 3,
            limit: 100));
        Assert.Equal(initial, staleCandidate.RevisionId);

        await CommitExistingStateAsync(initial, minute: 4);

        var status = await _retention.TryRetireCleanupCandidateAsync(
            staleCandidate,
            Now - TimeSpan.FromDays(7),
            retainedCanonicalHeadCount: 3,
            Now.AddMinutes(5));

        Assert.Equal(RetireSharedRevisionStatus.NoLongerEligible, status);
        Assert.NotNull(await _worldStore.LoadStateRevisionAsync(_world.WorldId, initial));
        var current = Assert.IsType<SharedWorldMetadata>(await _worldStore.LoadWorldAsync(_world.WorldId));
        Assert.Equal(initial, current.CurrentStateRevisionId);
        Assert.Empty(await _retention.ListPendingObjectCleanupAsync(DateTimeOffset.MaxValue, 100));
    }

    [Theory]
    [InlineData(SharedPackageTransferState.Active)]
    [InlineData(SharedPackageTransferState.IntegrityFailed)]
    [InlineData(SharedPackageTransferState.PublicationConflict)]
    public async Task UnresolvedTransferStatesPinNeverCommittedCandidate(
        SharedPackageTransferState transferState)
    {
        var revision = RevisionId.New();
        await RecordStateAsync(
            revision,
            Hex('B'),
            Now - TimeSpan.FromDays(8),
            requiredEnvironment: null);
        var transfer = CreateTransfer(
            revision,
            SharedPackageKind.State,
            transferState,
            requiredEnvironment: null);
        Assert.True(await _transfers.TryCreateAsync(transfer));

        var candidates = await _retention.ListCleanupEligibleAsync(
            Now - TimeSpan.FromDays(7),
            retainedCanonicalHeadCount: 3,
            limit: 100);

        Assert.DoesNotContain(candidates, item => item.RevisionId == revision);
    }

    [Fact]
    public async Task EnvironmentRetirementWaitsUntilStateReferenceIsRetired()
    {
        var environment = RevisionId.New();
        await RecordEnvironmentAsync(
            environment,
            Hex('E'),
            Now - TimeSpan.FromDays(8),
            hosted: true);
        var state = RevisionId.New();
        await RecordStateAsync(
            state,
            Hex('B'),
            Now - TimeSpan.FromDays(8),
            environment);

        var initialCandidates = await _retention.ListCleanupEligibleAsync(
            Now - TimeSpan.FromDays(7),
            retainedCanonicalHeadCount: 3,
            limit: 100);
        Assert.DoesNotContain(initialCandidates, item => item.RevisionId == environment);
        var stateCandidate = Assert.Single(initialCandidates, item => item.RevisionId == state);

        Assert.Equal(
            RetireSharedRevisionStatus.Retired,
            await _retention.TryRetireCleanupCandidateAsync(
                stateCandidate,
                Now - TimeSpan.FromDays(7),
                retainedCanonicalHeadCount: 3,
                Now));

        var afterStateRetired = await _retention.ListCleanupEligibleAsync(
            Now - TimeSpan.FromDays(7),
            retainedCanonicalHeadCount: 3,
            limit: 100);
        var environmentCandidate = Assert.Single(
            afterStateRetired,
            item => item.RevisionId == environment);

        Assert.Equal(
            RetireSharedRevisionStatus.Retired,
            await _retention.TryRetireCleanupCandidateAsync(
                environmentCandidate,
                Now - TimeSpan.FromDays(7),
                retainedCanonicalHeadCount: 3,
                Now));
        Assert.Null(await _worldStore.LoadEnvironmentRevisionAsync(_world.WorldId, environment));

        var queued = await _retention.ListPendingObjectCleanupAsync(DateTimeOffset.MaxValue, 100);
        Assert.Contains(queued, item => item.ObjectKey == $"packages/{_world.WorldId}/state/{state}.package");
        Assert.Contains(queued, item => item.ObjectKey == $"packages/{_world.WorldId}/environment/{environment}.package");
    }

    private async Task<RevisionId> CommitNewStateAsync(char hashCharacter, int minute)
    {
        var revision = RevisionId.New();
        await RecordStateAsync(revision, Hex(hashCharacter), Now.AddMinutes(minute), requiredEnvironment: null);
        await CommitExistingStateAsync(revision, minute);
        return revision;
    }

    private async Task CommitExistingStateAsync(RevisionId revision, int minute)
    {
        var current = Assert.IsType<SharedWorldMetadata>(await _worldStore.LoadWorldAsync(_world.WorldId));
        var serverNow = Now.AddMinutes(minute);
        var acquired = await _authority.AcquireAsync(
            _manager.Subject,
            _world.WorldId,
            "device-a",
            Head(current),
            serverNow,
            SharedWorldAuthorityOptions.FirstReleaseDefaults);
        var reservation = Assert.IsType<SharedWorldReservation>(acquired.Reservation);
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
    }

    private async Task RecordStateAsync(
        RevisionId revisionId,
        string sha256,
        DateTimeOffset publishedAt,
        RevisionId? requiredEnvironment)
    {
        var status = await _revisions.RecordVerifiedStateRevisionAsync(
            new SharedStateRevisionMetadata(
                _world.WorldId,
                revisionId,
                _world.AdapterId,
                $"packages/{_world.WorldId}/state/{revisionId}.package",
                4096,
                sha256,
                requiredEnvironment,
                _manager.Subject,
                publishedAt));
        Assert.True(status is RecordRevisionMetadataStatus.Recorded or RecordRevisionMetadataStatus.AlreadyRecorded);
    }

    private async Task RecordEnvironmentAsync(
        RevisionId revisionId,
        string sha256,
        DateTimeOffset publishedAt,
        bool hosted)
    {
        var status = await _revisions.RecordVerifiedEnvironmentRevisionAsync(
            new SharedEnvironmentRevisionMetadata(
                _world.WorldId,
                revisionId,
                _world.AdapterId,
                hosted
                    ? $"packages/{_world.WorldId}/environment/{revisionId}.package"
                    : $"native:{revisionId}",
                hosted ? 2048 : null,
                hosted ? sha256 : null,
                _manager.Subject,
                publishedAt));
        Assert.True(status is RecordRevisionMetadataStatus.Recorded or RecordRevisionMetadataStatus.AlreadyRecorded);
    }

    private SharedPackageTransferRecord CreateTransfer(
        RevisionId revision,
        SharedPackageKind kind,
        SharedPackageTransferState state,
        RevisionId? requiredEnvironment)
        => new(
            SharedPackageTransferId.New(),
            _world.WorldId,
            revision,
            kind,
            _world.AdapterId,
            _manager.Subject,
            $"packages/{_world.WorldId}/{kind.ToString().ToLowerInvariant()}/{revision}.package",
            $"provider-{Guid.NewGuid():N}",
            4096,
            Hex('B'),
            requiredEnvironment,
            1024,
            4,
            Now - TimeSpan.FromDays(8),
            Now - TimeSpan.FromDays(7),
            state,
            state == SharedPackageTransferState.Finalized ? Now - TimeSpan.FromDays(7) : null);

    private async Task ResetAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "TRUNCATE TABLE steward_object_cleanup_queue, steward_world_reservations, " +
            "steward_package_transfers, steward_world_invitations, steward_state_revisions, " +
            "steward_environment_revisions, steward_world_members, steward_shared_worlds CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private static SharedWorldHead Head(SharedWorldMetadata world)
        => new(world.CurrentStateRevisionId, world.CurrentEnvironmentRevisionId);

    private static string Hex(char character) => new(character, 64);

    private static VerifiedExternalIdentity Steam(string id)
        => new(new ExternalIdentityRef("steam", id));
}
