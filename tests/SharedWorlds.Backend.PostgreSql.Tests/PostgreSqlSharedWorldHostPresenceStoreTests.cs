using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlSharedWorldHostPresenceStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlSharedWorldHostPresenceStore _store = null!;
    private SharedWorldMetadataService _worlds = null!;
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
        await PostgreSqlSharedWorldHostPresenceSchema.InitializeAsync(_dataSource);
        await ResetAsync();

        var worldStore = new PostgreSqlSharedWorldStore(_dataSource);
        _worlds = new SharedWorldMetadataService(worldStore, () => Now);
        _store = new PostgreSqlSharedWorldHostPresenceStore(_dataSource);
        _manager = new VerifiedExternalIdentity(
            new ExternalIdentityRef("steam", "76561198000000001"),
            "Host");

        var created = await _worlds.CreateSharedWorldAsync(
            _manager,
            new CreateSharedWorldCommand(
                WorldId.New(),
                "factorio",
                "Presence World",
                RevisionId.New(),
                null));
        _world = Assert.IsType<SharedWorldMetadata>(created.World);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task TryUpsertRoundTripsExactActiveReservationPresence()
    {
        var sessionId = Guid.NewGuid();
        await SetReservationAsync(sessionId, 3, "device-a", SharedWorldReservationState.Active);
        var presence = Presence(
            sessionId,
            3,
            "device-a",
            SharedWorldHostPresenceState.Ready,
            "203.0.113.20",
            34197,
            "join-token",
            Now);

        Assert.True(await _store.TryUpsertAsync(presence));
        Assert.Equal(presence, await _store.GetAsync(_world.WorldId));
    }

    [Fact]
    public async Task NewReservationCannotBeOverwrittenOrClearedByStaleGeneration()
    {
        var oldSession = Guid.NewGuid();
        var currentSession = Guid.NewGuid();
        await SetReservationAsync(oldSession, 4, "device-a", SharedWorldReservationState.Active);
        var old = Presence(
            oldSession, 4, "device-a", SharedWorldHostPresenceState.Starting,
            null, null, null, Now);
        Assert.True(await _store.TryUpsertAsync(old));

        await SetReservationAsync(currentSession, 5, "device-b", SharedWorldReservationState.Active);
        var current = Presence(
            currentSession, 5, "device-b", SharedWorldHostPresenceState.Ready,
            "198.51.100.42", 34197, "new-token", Now.AddMinutes(1));
        Assert.True(await _store.TryUpsertAsync(current));

        Assert.False(await _store.TryUpsertAsync(old with
        {
            State = SharedWorldHostPresenceState.Ready,
            Address = "203.0.113.99",
            Port = 34197,
            UpdatedAt = Now.AddMinutes(2)
        }));
        Assert.False(await _store.DeleteAsync(
            _world.WorldId, _manager.Subject, "device-a", oldSession, 4));
        Assert.Equal(current, await _store.GetAsync(_world.WorldId));
    }

    [Fact]
    public async Task DifferentSessionCannotPublishAgainstCurrentReservation()
    {
        var currentSession = Guid.NewGuid();
        await SetReservationAsync(currentSession, 6, "device-a", SharedWorldReservationState.Active);
        var current = Presence(
            currentSession, 6, "device-a", SharedWorldHostPresenceState.Ready,
            "203.0.113.20", 34197, "current", Now);
        Assert.True(await _store.TryUpsertAsync(current));

        Assert.False(await _store.TryUpsertAsync(Presence(
            Guid.NewGuid(), 6, "device-b", SharedWorldHostPresenceState.Ready,
            "198.51.100.77", 34197, "wrong-session", Now.AddMinutes(1))));
        Assert.Equal(current, await _store.GetAsync(_world.WorldId));
    }

    [Fact]
    public async Task UncertainReservationCannotPublishPresence()
    {
        var sessionId = Guid.NewGuid();
        await SetReservationAsync(sessionId, 7, "device-a", SharedWorldReservationState.Uncertain);

        Assert.False(await _store.TryUpsertAsync(Presence(
            sessionId, 7, "device-a", SharedWorldHostPresenceState.Starting,
            null, null, null, Now)));
        Assert.Null(await _store.GetAsync(_world.WorldId));
    }

    [Fact]
    public async Task ExactClearRequiresInstallationIdentity()
    {
        var sessionId = Guid.NewGuid();
        await SetReservationAsync(sessionId, 8, "device-a", SharedWorldReservationState.Active);
        Assert.True(await _store.TryUpsertAsync(Presence(
            sessionId, 8, "device-a", SharedWorldHostPresenceState.Ready,
            "203.0.113.20", 34197, null, Now)));

        Assert.False(await _store.DeleteAsync(
            _world.WorldId, _manager.Subject, "device-b", sessionId, 8));
        Assert.NotNull(await _store.GetAsync(_world.WorldId));

        Assert.True(await _store.DeleteAsync(
            _world.WorldId, _manager.Subject, "device-a", sessionId, 8));
        Assert.Null(await _store.GetAsync(_world.WorldId));
        Assert.False(await _store.DeleteAsync(
            _world.WorldId, _manager.Subject, "device-a", sessionId, 8));
    }

    [Fact]
    public async Task UnknownWorldOrReservationIsRejectedWithoutMutation()
    {
        var unknown = new SharedWorldHostPresence(
            WorldId.New(),
            Guid.NewGuid(),
            1,
            _manager.Subject,
            "device-a",
            SharedWorldHostPresenceState.Starting,
            null,
            null,
            null,
            Now);

        Assert.False(await _store.TryUpsertAsync(unknown));
        Assert.Null(await _store.GetAsync(unknown.WorldId));
    }

    private SharedWorldHostPresence Presence(
        Guid sessionId,
        long generation,
        string installationId,
        SharedWorldHostPresenceState state,
        string? address,
        int? port,
        string? joinToken,
        DateTimeOffset updatedAt)
        => new(
            _world.WorldId,
            sessionId,
            generation,
            _manager.Subject,
            installationId,
            state,
            address,
            port,
            joinToken,
            updatedAt);

    private async Task SetReservationAsync(
        Guid sessionId,
        long generation,
        string installationId,
        SharedWorldReservationState state)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var delete = new NpgsqlCommand(
                         "DELETE FROM steward_world_reservations WHERE world_id = @world_id;",
                         connection,
                         transaction))
        {
            delete.Parameters.AddWithValue("world_id", _world.WorldId.Value);
            await delete.ExecuteNonQueryAsync();
        }

        await using (var insert = new NpgsqlCommand(
                         """
                         INSERT INTO steward_world_reservations (
                             world_id,
                             session_id,
                             generation,
                             holder_provider,
                             holder_external_id,
                             installation_id,
                             starting_state_revision_id,
                             starting_environment_revision_id,
                             state,
                             acquired_at,
                             last_heartbeat_at,
                             became_uncertain_at)
                         VALUES (
                             @world_id,
                             @session_id,
                             @generation,
                             @holder_provider,
                             @holder_external_id,
                             @installation_id,
                             @starting_state_revision_id,
                             @starting_environment_revision_id,
                             @state,
                             @now,
                             @now,
                             @became_uncertain_at);
                         """,
                         connection,
                         transaction))
        {
            insert.Parameters.AddWithValue("world_id", _world.WorldId.Value);
            insert.Parameters.AddWithValue("session_id", sessionId);
            insert.Parameters.AddWithValue("generation", generation);
            insert.Parameters.AddWithValue("holder_provider", _manager.Subject.Provider);
            insert.Parameters.AddWithValue("holder_external_id", _manager.Subject.ExternalId);
            insert.Parameters.AddWithValue("installation_id", installationId);
            insert.Parameters.AddWithValue("starting_state_revision_id", _world.CurrentStateRevisionId.Value);
            insert.Parameters.AddWithValue(
                "starting_environment_revision_id",
                NpgsqlTypes.NpgsqlDbType.Uuid,
                _world.CurrentEnvironmentRevisionId is null
                    ? DBNull.Value
                    : _world.CurrentEnvironmentRevisionId.Value.Value);
            insert.Parameters.AddWithValue("state", (short)state);
            insert.Parameters.AddWithValue("now", Now);
            insert.Parameters.AddWithValue(
                "became_uncertain_at",
                NpgsqlTypes.NpgsqlDbType.TimestampTz,
                state == SharedWorldReservationState.Uncertain ? Now : DBNull.Value);
            await insert.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private async Task ResetAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "TRUNCATE TABLE steward_world_host_presence, steward_world_reservations, " +
            "steward_package_transfers, steward_world_invitations, steward_state_revisions, " +
            "steward_environment_revisions, steward_world_members, steward_shared_worlds CASCADE;");
        await command.ExecuteNonQueryAsync();
    }
}
