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
    public async Task UpsertRoundTripsExactPresence()
    {
        var presence = Presence(
            sessionId: Guid.NewGuid(),
            generation: 3,
            installationId: "device-a",
            state: SharedWorldHostPresenceState.Ready,
            address: "203.0.113.20",
            port: 34197,
            joinToken: "join-token",
            updatedAt: Now);

        await _store.UpsertAsync(presence);

        var loaded = Assert.IsType<SharedWorldHostPresence>(
            await _store.GetAsync(_world.WorldId));
        Assert.Equal(presence, loaded);
    }

    [Fact]
    public async Task NewReservationCannotBeOverwrittenOrClearedByStaleGeneration()
    {
        var oldSession = Guid.NewGuid();
        var currentSession = Guid.NewGuid();
        var old = Presence(
            oldSession,
            generation: 4,
            installationId: "device-a",
            state: SharedWorldHostPresenceState.Starting,
            address: null,
            port: null,
            joinToken: null,
            updatedAt: Now);
        await _store.UpsertAsync(old);

        var current = Presence(
            currentSession,
            generation: 5,
            installationId: "device-b",
            state: SharedWorldHostPresenceState.Ready,
            address: "198.51.100.42",
            port: 34197,
            joinToken: "new-token",
            updatedAt: Now.AddMinutes(1));
        await _store.UpsertAsync(current);

        // Simulate a delayed request that passed service-level reservation validation before
        // generation 4 was reclaimed, but reached PostgreSQL only after generation 5 published.
        await _store.UpsertAsync(old with
        {
            State = SharedWorldHostPresenceState.Ready,
            Address = "203.0.113.99",
            Port = 34197,
            UpdatedAt = Now.AddMinutes(2)
        });

        Assert.False(await _store.DeleteAsync(
            _world.WorldId,
            _manager.Subject,
            oldSession,
            generation: 4));
        Assert.Equal(current, await _store.GetAsync(_world.WorldId));
    }

    [Fact]
    public async Task SameGenerationDifferentSessionCannotReplacePresence()
    {
        var currentSession = Guid.NewGuid();
        var current = Presence(
            currentSession,
            generation: 6,
            installationId: "device-a",
            state: SharedWorldHostPresenceState.Ready,
            address: "203.0.113.20",
            port: 34197,
            joinToken: "current",
            updatedAt: Now);
        await _store.UpsertAsync(current);

        await _store.UpsertAsync(Presence(
            Guid.NewGuid(),
            generation: 6,
            installationId: "device-b",
            state: SharedWorldHostPresenceState.Ready,
            address: "198.51.100.77",
            port: 34197,
            joinToken: "wrong-session",
            updatedAt: Now.AddMinutes(1)));

        Assert.Equal(current, await _store.GetAsync(_world.WorldId));
    }

    [Fact]
    public async Task ExactClearRemovesPresence()
    {
        var sessionId = Guid.NewGuid();
        await _store.UpsertAsync(Presence(
            sessionId,
            generation: 7,
            installationId: "device-a",
            state: SharedWorldHostPresenceState.Ready,
            address: "203.0.113.20",
            port: 34197,
            joinToken: null,
            updatedAt: Now));

        Assert.True(await _store.DeleteAsync(
            _world.WorldId,
            _manager.Subject,
            sessionId,
            generation: 7));
        Assert.Null(await _store.GetAsync(_world.WorldId));
        Assert.False(await _store.DeleteAsync(
            _world.WorldId,
            _manager.Subject,
            sessionId,
            generation: 7));
    }

    [Fact]
    public async Task PresenceCannotExistForUnknownWorld()
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

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            _store.UpsertAsync(unknown));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
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

    private async Task ResetAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "TRUNCATE TABLE steward_world_host_presence, steward_world_reservations, " +
            "steward_package_transfers, steward_world_invitations, steward_state_revisions, " +
            "steward_environment_revisions, steward_world_members, steward_shared_worlds CASCADE;");
        await command.ExecuteNonQueryAsync();
    }
}
