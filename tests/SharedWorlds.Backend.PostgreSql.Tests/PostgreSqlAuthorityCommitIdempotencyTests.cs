using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlAuthorityCommitIdempotencyTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly SharedWorldAuthorityOptions Options =
        SharedWorldAuthorityOptions.FirstReleaseDefaults;

    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlSharedWorldStore _worldStore = null!;
    private SharedWorldMetadataService _worlds = null!;
    private SharedRevisionMetadataService _revisions = null!;
    private PostgreSqlSharedWorldAuthorityStore _inner = null!;
    private PostgreSqlIdempotentSharedWorldAuthorityStore _authority = null!;
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
        _inner = new PostgreSqlSharedWorldAuthorityStore(_dataSource);
        _authority = new PostgreSqlIdempotentSharedWorldAuthorityStore(_dataSource, _inner);
        _manager = Steam("76561198000000001");

        var created = await _worlds.CreateSharedWorldAsync(
            _manager,
            new CreateSharedWorldCommand(
                WorldId.New(),
                "factorio",
                "Idempotent Commit World",
                RevisionId.New(),
                null));
        _world = Assert.IsType<SharedWorldMetadata>(created.World);
        await RecordStateAsync(_world.CurrentStateRevisionId, Hex('A'));
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task SuccessfulCommitReplaysOriginalResultAfterReservationIsGone()
    {
        var candidate = RevisionId.New();
        await RecordStateAsync(candidate, Hex('B'));
        var reservation = await AcquireAsync();
        var command = Command(reservation, candidate);
        var key = new StewardIdempotencyKey("commit-retry-1");

        var first = await _authority.CommitIdempotentAsync(
            _manager.Subject,
            command,
            key,
            Now.AddMinutes(1),
            Options);
        Assert.Equal(IdempotentMutationStatus.Executed, first.Status);
        Assert.Equal(CommitSharedWorldStatus.Committed, first.Result!.Status);
        Assert.Null(await _inner.GetReservationAsync(
            _manager.Subject,
            _world.WorldId,
            Now.AddMinutes(1),
            Options));

        var replay = await _authority.CommitIdempotentAsync(
            _manager.Subject,
            command,
            key,
            Now.AddMinutes(2),
            Options);

        Assert.Equal(IdempotentMutationStatus.Replayed, replay.Status);
        Assert.Equal(first.Result, replay.Result);
        var world = Assert.IsType<SharedWorldMetadata>(await _worldStore.LoadWorldAsync(_world.WorldId));
        Assert.Equal(candidate, world.CurrentStateRevisionId);
    }

    [Fact]
    public async Task SameKeyWithDifferentLogicalInputIsRejectedWithoutSecondMutation()
    {
        var firstCandidate = RevisionId.New();
        var conflictingCandidate = RevisionId.New();
        await RecordStateAsync(firstCandidate, Hex('B'));
        await RecordStateAsync(conflictingCandidate, Hex('C'));
        var reservation = await AcquireAsync();
        var key = new StewardIdempotencyKey("commit-conflict-1");

        var first = await _authority.CommitIdempotentAsync(
            _manager.Subject,
            Command(reservation, firstCandidate),
            key,
            Now.AddMinutes(1),
            Options);
        Assert.Equal(IdempotentMutationStatus.Executed, first.Status);

        var conflict = await _authority.CommitIdempotentAsync(
            _manager.Subject,
            Command(reservation, conflictingCandidate),
            key,
            Now.AddMinutes(2),
            Options);

        Assert.Equal(IdempotentMutationStatus.KeyConflict, conflict.Status);
        Assert.Null(conflict.Result);
        var world = Assert.IsType<SharedWorldMetadata>(await _worldStore.LoadWorldAsync(_world.WorldId));
        Assert.Equal(firstCandidate, world.CurrentStateRevisionId);
    }

    [Fact]
    public async Task ConcurrentSameKeyCommitExecutesOnceAndReplaysOnce()
    {
        var candidate = RevisionId.New();
        await RecordStateAsync(candidate, Hex('B'));
        var reservation = await AcquireAsync();
        var command = Command(reservation, candidate);
        var key = new StewardIdempotencyKey("commit-concurrent-1");

        var first = _authority.CommitIdempotentAsync(
            _manager.Subject,
            command,
            key,
            Now.AddMinutes(1),
            Options);
        var second = _authority.CommitIdempotentAsync(
            _manager.Subject,
            command,
            key,
            Now.AddMinutes(1),
            Options);

        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Status == IdempotentMutationStatus.Executed);
        Assert.Single(results, result => result.Status == IdempotentMutationStatus.Replayed);
        Assert.All(results, result => Assert.Equal(CommitSharedWorldStatus.Committed, result.Result!.Status));
        Assert.Equal(results[0].Result, results[1].Result);
    }

    private async Task<SharedWorldReservation> AcquireAsync()
    {
        var world = Assert.IsType<SharedWorldMetadata>(await _worldStore.LoadWorldAsync(_world.WorldId));
        var acquired = await _inner.AcquireAsync(
            _manager.Subject,
            _world.WorldId,
            "device-a",
            new SharedWorldHead(world.CurrentStateRevisionId, world.CurrentEnvironmentRevisionId),
            Now,
            Options);
        return Assert.IsType<SharedWorldReservation>(acquired.Reservation);
    }

    private CommitSharedWorldCommand Command(
        SharedWorldReservation reservation,
        RevisionId candidate)
        => new(
            _world.WorldId,
            reservation.SessionId,
            reservation.Generation,
            reservation.InstallationId,
            reservation.StartingHead,
            candidate,
            null);

    private async Task RecordStateAsync(RevisionId revisionId, string sha256)
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
                Now));
        Assert.True(status is RecordRevisionMetadataStatus.Recorded or RecordRevisionMetadataStatus.AlreadyRecorded);
    }

    private async Task ResetAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "TRUNCATE TABLE steward_authority_idempotency, steward_object_cleanup_queue, " +
            "steward_world_reservations, steward_package_transfers, steward_world_invitations, " +
            "steward_state_revisions, steward_environment_revisions, steward_world_members, " +
            "steward_shared_worlds CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private static string Hex(char character) => new(character, 64);

    private static VerifiedExternalIdentity Steam(string id)
        => new(new ExternalIdentityRef("steam", id));
}
