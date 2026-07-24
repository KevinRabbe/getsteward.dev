using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlLongSessionStressTests : IAsyncLifetime
{
    private const int HeartbeatSeconds = 30;
    private const int HeartbeatsPerDay = 24 * 60 * 60 / HeartbeatSeconds;

    private static readonly DateTimeOffset StartedAt =
        new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
    private static readonly SharedWorldAuthorityOptions Options =
        SharedWorldAuthorityOptions.FirstReleaseDefaults;

    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlSharedWorldAuthorityStore _authority = null!;
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
        var worlds = new SharedWorldMetadataService(worldStore, () => StartedAt);
        _authority = new PostgreSqlSharedWorldAuthorityStore(_dataSource);
        _manager = new VerifiedExternalIdentity(
            new ExternalIdentityRef("steam", "76561198999990201"),
            "Long Session Manager");

        var created = await worlds.CreateSharedWorldAsync(
            _manager,
            new CreateSharedWorldCommand(
                WorldId.New(),
                "factorio",
                "Long Session Stress World",
                RevisionId.New(),
                CurrentEnvironmentRevisionId: null));
        _world = Assert.IsType<SharedWorldMetadata>(created.World);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task TwentyFourHoursOfThirtySecondHeartbeatsKeepsOneActiveGeneration()
    {
        var head = new SharedWorldHead(
            _world.CurrentStateRevisionId,
            _world.CurrentEnvironmentRevisionId);
        var acquired = await _authority.AcquireAsync(
            _manager.Subject,
            _world.WorldId,
            "long-session-device",
            head,
            StartedAt,
            Options);
        Assert.Equal(AcquireSharedWorldReservationStatus.Acquired, acquired.Status);
        var reservation = Assert.IsType<SharedWorldReservation>(acquired.Reservation);
        Assert.Equal(1, reservation.Generation);

        var heartbeatAt = StartedAt;
        for (var heartbeat = 1; heartbeat <= HeartbeatsPerDay; heartbeat++)
        {
            heartbeatAt = heartbeatAt.AddSeconds(HeartbeatSeconds);
            var status = await _authority.HeartbeatAsync(
                _manager.Subject,
                _world.WorldId,
                "long-session-device",
                reservation.SessionId,
                reservation.Generation,
                heartbeatAt,
                Options);
            Assert.Equal(SharedWorldHeartbeatStatus.Accepted, status);
        }

        Assert.Equal(StartedAt.AddDays(1), heartbeatAt);
        var persisted = Assert.IsType<SharedWorldReservation>(
            await _authority.GetReservationAsync(
                _manager.Subject,
                _world.WorldId,
                heartbeatAt,
                Options));
        Assert.Equal(reservation.SessionId, persisted.SessionId);
        Assert.Equal(reservation.Generation, persisted.Generation);
        Assert.Equal(SharedWorldReservationState.Active, persisted.State);
        Assert.Equal(heartbeatAt, persisted.LastHeartbeatAt);
        Assert.Null(persisted.BecameUncertainAt);

        await using var command = _dataSource.CreateCommand(
            "SELECT COUNT(*) FROM steward_world_reservations WHERE world_id = @world_id;");
        command.Parameters.AddWithValue("world_id", _world.WorldId.Value);
        var rowCount = Convert.ToInt64(await command.ExecuteScalarAsync());
        Assert.Equal(1, rowCount);
    }

    private async Task ResetAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "TRUNCATE TABLE steward_world_reservations, steward_package_transfers, " +
            "steward_world_invitations, steward_state_revisions, steward_environment_revisions, " +
            "steward_world_members, steward_shared_worlds CASCADE;");
        await command.ExecuteNonQueryAsync();
    }
}
