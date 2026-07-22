using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlReservationIdempotencyTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly SharedWorldAuthorityOptions Options =
        SharedWorldAuthorityOptions.FirstReleaseDefaults;

    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlSharedWorldStore _worldStore = null!;
    private PostgreSqlSharedWorldAuthorityStore _baseAuthority = null!;
    private PostgreSqlIdempotentReservationAuthorityStore _authority = null!;
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
        _manager = Steam("76561198000000001");
        var worlds = new SharedWorldMetadataService(_worldStore, () => Now);
        var created = await worlds.CreateSharedWorldAsync(
            _manager,
            new CreateSharedWorldCommand(
                WorldId.New(),
                "factorio",
                "Idempotent Reservation World",
                RevisionId.New(),
                null));
        _world = Assert.IsType<SharedWorldMetadata>(created.World);

        _baseAuthority = new PostgreSqlSharedWorldAuthorityStore(_dataSource);
        var commitIdempotency = new PostgreSqlIdempotentSharedWorldAuthorityStore(
            _dataSource,
            _baseAuthority);
        _authority = new PostgreSqlIdempotentReservationAuthorityStore(
            _dataSource,
            commitIdempotency);
    }

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    [Fact]
    public async Task AcquireRetryReplaysOriginalAcquiredReservation()
    {
        var key = new StewardIdempotencyKey("acquire-retry-1");
        var head = Head(_world);

        var first = await _authority.AcquireIdempotentAsync(
            _manager.Subject,
            _world.WorldId,
            "device-a",
            head,
            key,
            Now,
            Options);
        var replay = await _authority.AcquireIdempotentAsync(
            _manager.Subject,
            _world.WorldId,
            "device-a",
            head,
            key,
            Now.AddMinutes(1),
            Options);

        Assert.Equal(IdempotentMutationStatus.Executed, first.Status);
        Assert.Equal(IdempotentMutationStatus.Replayed, replay.Status);
        Assert.Equal(AcquireSharedWorldReservationStatus.Acquired, first.Result!.Status);
        Assert.Equal(first.Result, replay.Result);
        Assert.Equal(first.Result.Reservation!.SessionId, replay.Result!.Reservation!.SessionId);
    }

    [Fact]
    public async Task ConcurrentSameKeyAcquireExecutesOnceAndReplaysOnce()
    {
        var key = new StewardIdempotencyKey("acquire-concurrent-1");
        var head = Head(_world);

        var first = _authority.AcquireIdempotentAsync(
            _manager.Subject,
            _world.WorldId,
            "device-a",
            head,
            key,
            Now,
            Options);
        var second = _authority.AcquireIdempotentAsync(
            _manager.Subject,
            _world.WorldId,
            "device-a",
            head,
            key,
            Now,
            Options);

        var results = await Task.WhenAll(first, second);
        Assert.Single(results, item => item.Status == IdempotentMutationStatus.Executed);
        Assert.Single(results, item => item.Status == IdempotentMutationStatus.Replayed);
        Assert.All(results, item => Assert.Equal(AcquireSharedWorldReservationStatus.Acquired, item.Result!.Status));
        Assert.Equal(results[0].Result, results[1].Result);
    }

    [Fact]
    public async Task AcquireKeyReuseWithDifferentInputIsConflict()
    {
        var key = new StewardIdempotencyKey("acquire-conflict-1");
        var head = Head(_world);
        var first = await _authority.AcquireIdempotentAsync(
            _manager.Subject,
            _world.WorldId,
            "device-a",
            head,
            key,
            Now,
            Options);
        Assert.Equal(IdempotentMutationStatus.Executed, first.Status);

        var conflict = await _authority.AcquireIdempotentAsync(
            _manager.Subject,
            _world.WorldId,
            "device-b",
            head,
            key,
            Now.AddMinutes(1),
            Options);

        Assert.Equal(IdempotentMutationStatus.KeyConflict, conflict.Status);
        Assert.Null(conflict.Result);
    }

    [Fact]
    public async Task ReclaimRetryReplaysOriginalResultAfterReservationDeletion()
    {
        var acquired = await _baseAuthority.AcquireAsync(
            _manager.Subject,
            _world.WorldId,
            "device-a",
            Head(_world),
            Now,
            Options);
        var reservation = Assert.IsType<SharedWorldReservation>(acquired.Reservation);
        var uncertainAt = Now + Options.UncertaintyAfter;
        Assert.NotNull(await _baseAuthority.GetReservationAsync(
            _manager.Subject,
            _world.WorldId,
            uncertainAt,
            Options));
        var reclaimAt = uncertainAt + Options.ReclaimAfterUncertain;
        var key = new StewardIdempotencyKey("reclaim-retry-1");

        var first = await _authority.ReclaimIdempotentAsync(
            _manager.Subject,
            _world.WorldId,
            reservation.SessionId,
            reservation.Generation,
            key,
            reclaimAt,
            Options);
        Assert.Equal(IdempotentMutationStatus.Executed, first.Status);
        Assert.Equal(ReclaimSharedWorldReservationStatus.Reclaimed, first.Result!.Status);
        Assert.Null(await _baseAuthority.GetReservationAsync(
            _manager.Subject,
            _world.WorldId,
            reclaimAt,
            Options));

        var replay = await _authority.ReclaimIdempotentAsync(
            _manager.Subject,
            _world.WorldId,
            reservation.SessionId,
            reservation.Generation,
            key,
            reclaimAt.AddMinutes(1),
            Options);

        Assert.Equal(IdempotentMutationStatus.Replayed, replay.Status);
        Assert.Equal(first.Result, replay.Result);
    }

    [Fact]
    public async Task ReclaimKeyReuseWithDifferentGenerationIsConflict()
    {
        var acquired = await _baseAuthority.AcquireAsync(
            _manager.Subject,
            _world.WorldId,
            "device-a",
            Head(_world),
            Now,
            Options);
        var reservation = Assert.IsType<SharedWorldReservation>(acquired.Reservation);
        var uncertainAt = Now + Options.UncertaintyAfter;
        Assert.NotNull(await _baseAuthority.GetReservationAsync(
            _manager.Subject,
            _world.WorldId,
            uncertainAt,
            Options));
        var key = new StewardIdempotencyKey("reclaim-conflict-1");

        var first = await _authority.ReclaimIdempotentAsync(
            _manager.Subject,
            _world.WorldId,
            reservation.SessionId,
            reservation.Generation,
            key,
            uncertainAt + Options.ReclaimAfterUncertain,
            Options);
        Assert.Equal(IdempotentMutationStatus.Executed, first.Status);

        var conflict = await _authority.ReclaimIdempotentAsync(
            _manager.Subject,
            _world.WorldId,
            reservation.SessionId,
            reservation.Generation + 1,
            key,
            uncertainAt + Options.ReclaimAfterUncertain + TimeSpan.FromMinutes(1),
            Options);

        Assert.Equal(IdempotentMutationStatus.KeyConflict, conflict.Status);
        Assert.Null(conflict.Result);
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

    private static SharedWorldHead Head(SharedWorldMetadata world)
        => new(world.CurrentStateRevisionId, world.CurrentEnvironmentRevisionId);

    private static VerifiedExternalIdentity Steam(string id)
        => new(new ExternalIdentityRef("steam", id));
}
