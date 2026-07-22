using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlSharedWorldReservationAbandonStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlSharedWorldAuthorityStore _authority = null!;
    private PostgreSqlSharedWorldReservationAbandonStore _abandon = null!;
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

        var worldStore = new PostgreSqlSharedWorldStore(_dataSource);
        var worlds = new SharedWorldMetadataService(worldStore, () => Now);
        _authority = new PostgreSqlSharedWorldAuthorityStore(_dataSource);
        _abandon = new PostgreSqlSharedWorldReservationAbandonStore(_dataSource);
        _manager = Steam("76561198000000001");

        var created = await worlds.CreateSharedWorldAsync(
            _manager,
            new CreateSharedWorldCommand(
                WorldId.New(),
                "factorio",
                "Abandon World",
                RevisionId.New(),
                null));
        _world = Assert.IsType<SharedWorldMetadata>(created.World);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task ExactHolderCanAbandonAndRetryConverges()
    {
        var reservation = await AcquireAsync("device-a");

        Assert.Equal(
            AbandonSharedWorldReservationStatus.Abandoned,
            await _abandon.AbandonAsync(
                _manager.Subject,
                _world.WorldId,
                "device-a",
                reservation.SessionId,
                reservation.Generation));
        Assert.Null(await CurrentReservationAsync());

        Assert.Equal(
            AbandonSharedWorldReservationStatus.NoLongerCurrent,
            await _abandon.AbandonAsync(
                _manager.Subject,
                _world.WorldId,
                "device-a",
                reservation.SessionId,
                reservation.Generation));
    }

    [Fact]
    public async Task MismatchedReservationIdentityCannotReleaseCurrentWriter()
    {
        var reservation = await AcquireAsync("device-a");

        Assert.Equal(
            AbandonSharedWorldReservationStatus.NoLongerCurrent,
            await _abandon.AbandonAsync(
                _manager.Subject,
                _world.WorldId,
                "device-b",
                reservation.SessionId,
                reservation.Generation));
        Assert.Equal(
            AbandonSharedWorldReservationStatus.NoLongerCurrent,
            await _abandon.AbandonAsync(
                Steam("76561198000000002").Subject,
                _world.WorldId,
                "device-a",
                reservation.SessionId,
                reservation.Generation));
        Assert.Equal(
            AbandonSharedWorldReservationStatus.NoLongerCurrent,
            await _abandon.AbandonAsync(
                _manager.Subject,
                _world.WorldId,
                "device-a",
                Guid.NewGuid(),
                reservation.Generation));
        Assert.Equal(
            AbandonSharedWorldReservationStatus.NoLongerCurrent,
            await _abandon.AbandonAsync(
                _manager.Subject,
                _world.WorldId,
                "device-a",
                reservation.SessionId,
                reservation.Generation + 1));

        var current = Assert.IsType<SharedWorldReservation>(await CurrentReservationAsync());
        Assert.Equal(reservation.SessionId, current.SessionId);
        Assert.Equal(reservation.Generation, current.Generation);
    }

    [Fact]
    public async Task DelayedOldGenerationRetryCannotReleaseNewWriter()
    {
        var first = await AcquireAsync("device-a");
        Assert.Equal(
            AbandonSharedWorldReservationStatus.Abandoned,
            await _abandon.AbandonAsync(
                _manager.Subject,
                _world.WorldId,
                "device-a",
                first.SessionId,
                first.Generation));

        var second = await AcquireAsync("device-a");
        Assert.True(second.Generation > first.Generation);
        Assert.NotEqual(first.SessionId, second.SessionId);

        Assert.Equal(
            AbandonSharedWorldReservationStatus.NoLongerCurrent,
            await _abandon.AbandonAsync(
                _manager.Subject,
                _world.WorldId,
                "device-a",
                first.SessionId,
                first.Generation));

        var current = Assert.IsType<SharedWorldReservation>(await CurrentReservationAsync());
        Assert.Equal(second.SessionId, current.SessionId);
        Assert.Equal(second.Generation, current.Generation);
    }

    private async Task<SharedWorldReservation> AcquireAsync(string installationId)
    {
        var acquired = await _authority.AcquireAsync(
            _manager.Subject,
            _world.WorldId,
            installationId,
            new SharedWorldHead(
                _world.CurrentStateRevisionId,
                _world.CurrentEnvironmentRevisionId),
            Now,
            SharedWorldAuthorityOptions.FirstReleaseDefaults);
        Assert.Equal(AcquireSharedWorldReservationStatus.Acquired, acquired.Status);
        return Assert.IsType<SharedWorldReservation>(acquired.Reservation);
    }

    private Task<SharedWorldReservation?> CurrentReservationAsync()
        => _authority.GetReservationAsync(
            _manager.Subject,
            _world.WorldId,
            Now,
            SharedWorldAuthorityOptions.FirstReleaseDefaults);

    private async Task ResetAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "TRUNCATE TABLE steward_world_reservations, steward_package_transfers, " +
            "steward_world_invitations, steward_state_revisions, steward_environment_revisions, " +
            "steward_world_members, steward_shared_worlds CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private static VerifiedExternalIdentity Steam(string id)
        => new(new ExternalIdentityRef("steam", id));
}
