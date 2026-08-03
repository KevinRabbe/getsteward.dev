using Npgsql;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlOwnedWorldSnapshotStoreTests
{
    private const string PackageSha256 =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public async Task ExactSnapshotRoundTripsIdempotentlyAndConflictsOnDifferentMetadata()
    {
        await WithStoresAsync(async (locationStore, snapshotStore, owner, installationId) =>
        {
            var worldId = WorldId.New();
            var stateId = RevisionId.New();
            var environmentId = RevisionId.New();
            await PublishLocationAsync(
                locationStore,
                owner,
                installationId,
                worldId,
                stateId,
                environmentId);
            var registry = new OwnedWorldSnapshotRegistry(locationStore, snapshotStore);
            var publishedAt = TruncateToMicroseconds(DateTimeOffset.UtcNow);
            var manifest = Manifest();

            var created = await registry.PublishAsync(
                owner,
                installationId,
                worldId,
                stateId,
                environmentId,
                "factorio",
                $"private/{worldId}/{stateId}",
                100,
                PackageSha256.ToLowerInvariant(),
                manifest,
                publishedAt);
            var repeated = await registry.PublishAsync(
                owner,
                installationId,
                worldId,
                stateId,
                environmentId,
                "factorio",
                $"private/{worldId}/{stateId}",
                100,
                PackageSha256,
                manifest,
                publishedAt);
            var conflict = await registry.PublishAsync(
                owner,
                installationId,
                worldId,
                stateId,
                environmentId,
                "factorio",
                $"private/{worldId}/{stateId}",
                101,
                PackageSha256,
                manifest,
                publishedAt);

            Assert.Equal(OwnedWorldSnapshotWriteResult.Created, created.Result);
            Assert.Equal(OwnedWorldSnapshotWriteResult.NoChange, repeated.Result);
            Assert.Equal(OwnedWorldSnapshotWriteResult.Conflict, conflict.Result);
            Assert.Equal(100, conflict.Current.StatePackageByteSize);

            var loaded = await snapshotStore.LoadExactAsync(
                owner.Provider,
                owner.ExternalId,
                worldId,
                installationId,
                stateId,
                environmentId);
            Assert.NotNull(loaded);
            Assert.Equal(PackageSha256, loaded.StatePackageSha256);
            Assert.Equal(manifest.SchemaVersion, loaded.EnvironmentManifest.SchemaVersion);
            Assert.Equal(manifest.AdapterId, loaded.EnvironmentManifest.AdapterId);
        });
    }

    [Fact]
    public async Task StoreRejectsStaleHeadAfterLocationAdvancesButPreservesPublishedHistory()
    {
        await WithStoresAsync(async (locationStore, snapshotStore, owner, installationId) =>
        {
            var worldId = WorldId.New();
            var firstState = RevisionId.New();
            var firstEnvironment = RevisionId.New();
            await PublishLocationAsync(
                locationStore,
                owner,
                installationId,
                worldId,
                firstState,
                firstEnvironment);
            var registry = new OwnedWorldSnapshotRegistry(locationStore, snapshotStore);
            var first = await registry.PublishAsync(
                owner,
                installationId,
                worldId,
                firstState,
                firstEnvironment,
                "factorio",
                $"private/{worldId}/{firstState}",
                100,
                PackageSha256,
                Manifest(),
                TruncateToMicroseconds(DateTimeOffset.UtcNow));
            Assert.Equal(OwnedWorldSnapshotWriteResult.Created, first.Result);

            var nextState = RevisionId.New();
            var nextEnvironment = RevisionId.New();
            var locationRegistry = new OwnedWorldLocationRegistry(locationStore);
            var advanced = await locationRegistry.PublishLocationWithPresentationAsync(
                owner,
                installationId,
                worldId,
                nextState,
                nextEnvironment,
                "Factory World",
                "factorio",
                DateTimeOffset.UtcNow.AddMinutes(1),
                expectedStateRevisionId: firstState,
                expectedEnvironmentRevisionId: firstEnvironment);
            Assert.Equal(OwnedWorldLocationWriteResult.Updated, advanced.Result);

            var stale = Snapshot(
                owner,
                installationId,
                worldId,
                firstState,
                firstEnvironment,
                $"private/{worldId}/{firstState}-late");
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                snapshotStore.PublishAsync(stale));
            Assert.Contains("changed its private World head", exception.Message, StringComparison.Ordinal);

            var preserved = await snapshotStore.LoadExactAsync(
                owner.Provider,
                owner.ExternalId,
                worldId,
                installationId,
                firstState,
                firstEnvironment);
            Assert.NotNull(preserved);

            var next = await registry.PublishAsync(
                owner,
                installationId,
                worldId,
                nextState,
                nextEnvironment,
                "factorio",
                $"private/{worldId}/{nextState}",
                100,
                PackageSha256,
                Manifest(),
                TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(1)));
            Assert.Equal(OwnedWorldSnapshotWriteResult.Created, next.Result);
            Assert.Equal(2, (await snapshotStore.ListWorldSnapshotsAsync(
                owner.Provider,
                owner.ExternalId,
                worldId,
                maximumSnapshots: 10)).Count);
        });
    }

    [Fact]
    public async Task ConcurrentConflictingFirstPublicationProducesCreatedAndConflict()
    {
        await WithStoresAsync(async (locationStore, snapshotStore, owner, installationId) =>
        {
            var worldId = WorldId.New();
            var stateId = RevisionId.New();
            var environmentId = RevisionId.New();
            await PublishLocationAsync(
                locationStore,
                owner,
                installationId,
                worldId,
                stateId,
                environmentId);
            var publishedAt = TruncateToMicroseconds(DateTimeOffset.UtcNow);
            var first = Snapshot(
                owner,
                installationId,
                worldId,
                stateId,
                environmentId,
                $"private/{worldId}/first") with
            {
                PublishedAt = publishedAt
            };
            var second = first with
            {
                StatePackageObjectKey = $"private/{worldId}/second"
            };

            var results = await Task.WhenAll(
                snapshotStore.PublishAsync(first),
                snapshotStore.PublishAsync(second));

            Assert.Contains(results, result =>
                result.Result == OwnedWorldSnapshotWriteResult.Created);
            Assert.Contains(results, result =>
                result.Result == OwnedWorldSnapshotWriteResult.Conflict);
            Assert.Single(await snapshotStore.ListWorldSnapshotsAsync(
                owner.Provider,
                owner.ExternalId,
                worldId,
                maximumSnapshots: 10));
        });
    }

    [Fact]
    public async Task SnapshotQueriesRemainOwnerScoped()
    {
        var connectionString = Environment.GetEnvironmentVariable("STEWARD_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var locationStore = new PostgreSqlOwnedWorldLocationStore(dataSource);
        await locationStore.InitializeAsync();
        var snapshotStore = new PostgreSqlOwnedWorldSnapshotStore(dataSource);
        await snapshotStore.InitializeAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var worldId = WorldId.New();
        var firstOwner = new UserIdentity("steam", $"owner-a-{suffix}", "A");
        var secondOwner = new UserIdentity("steam", $"owner-b-{suffix}", "B");

        await PublishOwnedSnapshotAsync(
            locationStore,
            snapshotStore,
            firstOwner,
            $"pc-a-{suffix}",
            worldId);
        await PublishOwnedSnapshotAsync(
            locationStore,
            snapshotStore,
            secondOwner,
            $"pc-b-{suffix}",
            worldId);

        var first = await snapshotStore.ListWorldSnapshotsAsync(
            firstOwner.Provider,
            firstOwner.ExternalId,
            worldId,
            maximumSnapshots: 10);
        var second = await snapshotStore.ListWorldSnapshotsAsync(
            secondOwner.Provider,
            secondOwner.ExternalId,
            worldId,
            maximumSnapshots: 10);

        Assert.Single(first);
        Assert.Single(second);
        Assert.All(first, snapshot => Assert.Equal(firstOwner.ExternalId, snapshot.OwnerExternalId));
        Assert.All(second, snapshot => Assert.Equal(secondOwner.ExternalId, snapshot.OwnerExternalId));
    }

    [Fact]
    public async Task MalformedPersistedEnvironmentManifestFailsClosedOnRead()
    {
        var connectionString = Environment.GetEnvironmentVariable("STEWARD_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var locationStore = new PostgreSqlOwnedWorldLocationStore(dataSource);
        await locationStore.InitializeAsync();
        var snapshotStore = new PostgreSqlOwnedWorldSnapshotStore(dataSource);
        await snapshotStore.InitializeAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var owner = new UserIdentity("steam", $"owner-{suffix}", "Owner");
        var installationId = $"pc-{suffix}";
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        await RegisterAsync(locationStore, owner, installationId);
        await PublishLocationAsync(
            locationStore,
            owner,
            installationId,
            worldId,
            stateId,
            environmentId);
        var registry = new OwnedWorldSnapshotRegistry(locationStore, snapshotStore);
        await registry.PublishAsync(
            owner,
            installationId,
            worldId,
            stateId,
            environmentId,
            "factorio",
            $"private/{worldId}/{stateId}",
            100,
            PackageSha256,
            Manifest(),
            TruncateToMicroseconds(DateTimeOffset.UtcNow));

        await using (var command = dataSource.CreateCommand("""
            UPDATE steward_owned_world_snapshots
               SET environment_manifest_json = '{}'::jsonb
             WHERE world_id = @world_id
               AND owner_provider = @owner_provider
               AND owner_external_id = @owner_external_id
               AND installation_id = @installation_id
               AND state_revision_id = @state_revision_id
               AND environment_revision_id = @environment_revision_id;
            """))
        {
            command.Parameters.AddWithValue("world_id", worldId.Value);
            command.Parameters.AddWithValue("owner_provider", owner.Provider);
            command.Parameters.AddWithValue("owner_external_id", owner.ExternalId);
            command.Parameters.AddWithValue("installation_id", installationId);
            command.Parameters.AddWithValue("state_revision_id", stateId.Value);
            command.Parameters.AddWithValue("environment_revision_id", environmentId.Value);
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => snapshotStore.LoadExactAsync(
            owner.Provider,
            owner.ExternalId,
            worldId,
            installationId,
            stateId,
            environmentId));
    }

    private static async Task WithStoresAsync(
        Func<PostgreSqlOwnedWorldLocationStore,
            PostgreSqlOwnedWorldSnapshotStore,
            UserIdentity,
            string,
            Task> test)
    {
        var connectionString = Environment.GetEnvironmentVariable("STEWARD_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var locationStore = new PostgreSqlOwnedWorldLocationStore(dataSource);
        await locationStore.InitializeAsync();
        var snapshotStore = new PostgreSqlOwnedWorldSnapshotStore(dataSource);
        await snapshotStore.InitializeAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var owner = new UserIdentity("steam", $"owner-{suffix}", "Owner");
        var installationId = $"pc-{suffix}";
        await RegisterAsync(locationStore, owner, installationId);
        await test(locationStore, snapshotStore, owner, installationId);
    }

    private static async Task PublishOwnedSnapshotAsync(
        PostgreSqlOwnedWorldLocationStore locationStore,
        PostgreSqlOwnedWorldSnapshotStore snapshotStore,
        UserIdentity owner,
        string installationId,
        WorldId worldId)
    {
        await RegisterAsync(locationStore, owner, installationId);
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        await PublishLocationAsync(
            locationStore,
            owner,
            installationId,
            worldId,
            stateId,
            environmentId);
        var registry = new OwnedWorldSnapshotRegistry(locationStore, snapshotStore);
        var result = await registry.PublishAsync(
            owner,
            installationId,
            worldId,
            stateId,
            environmentId,
            "factorio",
            $"private/{owner.ExternalId}/{worldId}/{stateId}",
            100,
            PackageSha256,
            Manifest(),
            TruncateToMicroseconds(DateTimeOffset.UtcNow));
        Assert.Equal(OwnedWorldSnapshotWriteResult.Created, result.Result);
    }

    private static async Task RegisterAsync(
        PostgreSqlOwnedWorldLocationStore store,
        UserIdentity owner,
        string installationId)
    {
        var registry = new OwnedWorldLocationRegistry(store);
        await registry.RegisterInstallationAsync(
            owner,
            installationId,
            installationId,
            DateTimeOffset.UtcNow);
    }

    private static async Task PublishLocationAsync(
        PostgreSqlOwnedWorldLocationStore store,
        UserIdentity owner,
        string installationId,
        WorldId worldId,
        RevisionId stateId,
        RevisionId environmentId)
    {
        var registry = new OwnedWorldLocationRegistry(store);
        var result = await registry.PublishLocationWithPresentationAsync(
            owner,
            installationId,
            worldId,
            stateId,
            environmentId,
            "Factory World",
            "factorio",
            DateTimeOffset.UtcNow);
        Assert.Equal(OwnedWorldLocationWriteResult.Created, result.Result);
    }

    private static OwnedWorldSnapshot Snapshot(
        UserIdentity owner,
        string installationId,
        WorldId worldId,
        RevisionId stateId,
        RevisionId environmentId,
        string objectKey)
        => new(
            worldId,
            owner.Provider,
            owner.ExternalId,
            installationId,
            stateId,
            environmentId,
            "factorio",
            objectKey,
            100,
            PackageSha256,
            Manifest(),
            TruncateToMicroseconds(DateTimeOffset.UtcNow));

    private static EnvironmentManifest Manifest()
        => new(
            SchemaVersion: 1,
            AdapterId: "factorio",
            GameVersion: "2.0.0",
            Components: [],
            Configuration: new Dictionary<string, string>());

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
        => new(value.Ticks - (value.Ticks % 10), value.Offset);
}
