using Npgsql;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlOwnedWorldSnapshotRevisionEvidenceStoreTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
    private const string PackageSha256 =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Fact]
    public async Task ExactEvidenceRoundTripsIdempotentlyAndConflictsOnDifferentRecord()
    {
        await WithStoresAsync(async context =>
        {
            var fixture = await PublishSnapshotAsync(context);
            var registry = new OwnedWorldSnapshotRevisionEvidenceRegistry(
                context.SnapshotStore,
                context.EvidenceStore);
            var evidence = Evidence(context, fixture);

            var created = await registry.PublishAsync(context.Owner, evidence);
            var repeated = await registry.PublishAsync(context.Owner, evidence);
            var conflict = await registry.PublishAsync(
                context.Owner,
                evidence with
                {
                    StateRevision = evidence.StateRevision with
                    {
                        StatePackageId = "different-package-identity"
                    }
                });

            Assert.Equal(
                OwnedWorldSnapshotRevisionEvidenceWriteResult.Created,
                created.Result);
            Assert.Equal(
                OwnedWorldSnapshotRevisionEvidenceWriteResult.NoChange,
                repeated.Result);
            Assert.Equal(
                OwnedWorldSnapshotRevisionEvidenceWriteResult.Conflict,
                conflict.Result);
            Assert.Equal(
                evidence.StateRevision.StatePackageId,
                conflict.Current.StateRevision.StatePackageId);

            var loaded = await context.EvidenceStore.LoadExactAsync(
                context.Owner.Provider,
                context.Owner.ExternalId,
                fixture.WorldId,
                context.InstallationId,
                fixture.StateId,
                fixture.EnvironmentId);
            Assert.NotNull(loaded);
            Assert.Equal(evidence.StateRevision.Id, loaded.StateRevision.Id);
            Assert.Equal(
                evidence.StateRevision.ParentRevisionId,
                loaded.StateRevision.ParentRevisionId);
            Assert.Equal(
                evidence.StateRevision.StatePackageId,
                loaded.StateRevision.StatePackageId);
            Assert.Equal(
                evidence.StateRevision.CreatedBy,
                loaded.StateRevision.CreatedBy);
            Assert.Equal(
                evidence.EnvironmentRevision.Id,
                loaded.EnvironmentRevision.Id);
            Assert.Equal(
                evidence.EnvironmentRevision.ParentRevisionId,
                loaded.EnvironmentRevision.ParentRevisionId);
            Assert.Equal(
                evidence.EnvironmentRevision.CreatedBy,
                loaded.EnvironmentRevision.CreatedBy);
            Assert.Equal(
                evidence.EnvironmentRevision.Manifest.AdapterId,
                loaded.EnvironmentRevision.Manifest.AdapterId);
            Assert.Equal(
                TruncateToMicroseconds(evidence.RecordedAt),
                loaded.RecordedAt);
        });
    }

    [Fact]
    public async Task EvidenceCannotExistWithoutExactVerifiedSnapshot()
    {
        await WithStoresAsync(async context =>
        {
            var fixture = await PublishLocationOnlyAsync(context);
            var evidence = Evidence(context, fixture);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                context.EvidenceStore.PublishAsync(evidence));

            Assert.Contains(
                "snapshot bytes must exist",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Empty(await context.EvidenceStore.ListWorldEvidenceAsync(
                context.Owner.Provider,
                context.Owner.ExternalId,
                fixture.WorldId,
                maximumEvidence: 10));
        });
    }

    [Fact]
    public async Task LegacySnapshotWithoutEvidenceRemainsExplicitlyAbsent()
    {
        await WithStoresAsync(async context =>
        {
            var fixture = await PublishSnapshotAsync(context);

            var loaded = await context.EvidenceStore.LoadExactAsync(
                context.Owner.Provider,
                context.Owner.ExternalId,
                fixture.WorldId,
                context.InstallationId,
                fixture.StateId,
                fixture.EnvironmentId);
            var listed = await context.EvidenceStore.ListWorldEvidenceAsync(
                context.Owner.Provider,
                context.Owner.ExternalId,
                fixture.WorldId,
                maximumEvidence: 10);

            Assert.Null(loaded);
            Assert.Empty(listed);
        });
    }

    [Fact]
    public async Task ConcurrentConflictingFirstPublicationProducesCreatedAndConflict()
    {
        await WithStoresAsync(async context =>
        {
            var fixture = await PublishSnapshotAsync(context);
            var first = Evidence(context, fixture);
            var second = first with
            {
                StateRevision = first.StateRevision with
                {
                    StatePackageId = "concurrent-different-package"
                }
            };

            var results = await Task.WhenAll(
                context.EvidenceStore.PublishAsync(first),
                context.EvidenceStore.PublishAsync(second));

            Assert.Contains(results, result =>
                result.Result ==
                OwnedWorldSnapshotRevisionEvidenceWriteResult.Created);
            Assert.Contains(results, result =>
                result.Result ==
                OwnedWorldSnapshotRevisionEvidenceWriteResult.Conflict);
            Assert.Single(await context.EvidenceStore.ListWorldEvidenceAsync(
                context.Owner.Provider,
                context.Owner.ExternalId,
                fixture.WorldId,
                maximumEvidence: 10));
        });
    }

    [Fact]
    public async Task EvidenceQueriesRemainOwnerScoped()
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
        var evidenceStore = new PostgreSqlOwnedWorldSnapshotRevisionEvidenceStore(dataSource);
        await evidenceStore.InitializeAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var worldId = WorldId.New();
        var first = CreateContext(
            dataSource,
            locationStore,
            snapshotStore,
            evidenceStore,
            new UserIdentity("steam", $"owner-a-{suffix}", "A"),
            $"pc-a-{suffix}");
        var second = CreateContext(
            dataSource,
            locationStore,
            snapshotStore,
            evidenceStore,
            new UserIdentity("steam", $"owner-b-{suffix}", "B"),
            $"pc-b-{suffix}");
        await RegisterAsync(locationStore, first.Owner, first.InstallationId);
        await RegisterAsync(locationStore, second.Owner, second.InstallationId);
        var firstFixture = await PublishSnapshotAsync(first, worldId);
        var secondFixture = await PublishSnapshotAsync(second, worldId);
        await evidenceStore.PublishAsync(Evidence(first, firstFixture));
        await evidenceStore.PublishAsync(Evidence(second, secondFixture));

        var firstResults = await evidenceStore.ListWorldEvidenceAsync(
            first.Owner.Provider,
            first.Owner.ExternalId,
            worldId,
            maximumEvidence: 10);
        var secondResults = await evidenceStore.ListWorldEvidenceAsync(
            second.Owner.Provider,
            second.Owner.ExternalId,
            worldId,
            maximumEvidence: 10);

        Assert.Single(firstResults);
        Assert.Single(secondResults);
        Assert.All(firstResults, evidence =>
            Assert.Equal(first.Owner.ExternalId, evidence.OwnerExternalId));
        Assert.All(secondResults, evidence =>
            Assert.Equal(second.Owner.ExternalId, evidence.OwnerExternalId));
    }

    [Fact]
    public async Task MalformedPersistedRevisionJsonFailsClosedOnRead()
    {
        await WithStoresAsync(async context =>
        {
            var fixture = await PublishSnapshotAsync(context);
            await context.EvidenceStore.PublishAsync(Evidence(context, fixture));

            await using (var command = context.DataSource.CreateCommand("""
                UPDATE steward_owned_world_snapshot_revision_evidence
                   SET state_revision_json = '{}'::jsonb
                 WHERE world_id = @world_id
                   AND owner_provider = @owner_provider
                   AND owner_external_id = @owner_external_id
                   AND installation_id = @installation_id
                   AND state_revision_id = @state_revision_id
                   AND environment_revision_id = @environment_revision_id;
                """))
            {
                command.Parameters.AddWithValue("world_id", fixture.WorldId.Value);
                command.Parameters.AddWithValue("owner_provider", context.Owner.Provider);
                command.Parameters.AddWithValue("owner_external_id", context.Owner.ExternalId);
                command.Parameters.AddWithValue("installation_id", context.InstallationId);
                command.Parameters.AddWithValue("state_revision_id", fixture.StateId.Value);
                command.Parameters.AddWithValue(
                    "environment_revision_id",
                    fixture.EnvironmentId.Value);
                await command.ExecuteNonQueryAsync();
            }

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                context.EvidenceStore.LoadExactAsync(
                    context.Owner.Provider,
                    context.Owner.ExternalId,
                    fixture.WorldId,
                    context.InstallationId,
                    fixture.StateId,
                    fixture.EnvironmentId));
        });
    }

    private static async Task WithStoresAsync(Func<StoreContext, Task> test)
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
        var evidenceStore = new PostgreSqlOwnedWorldSnapshotRevisionEvidenceStore(dataSource);
        await evidenceStore.InitializeAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var owner = new UserIdentity("steam", $"owner-{suffix}", "Owner");
        var installationId = $"pc-{suffix}";
        await RegisterAsync(locationStore, owner, installationId);
        await test(CreateContext(
            dataSource,
            locationStore,
            snapshotStore,
            evidenceStore,
            owner,
            installationId));
    }

    private static StoreContext CreateContext(
        NpgsqlDataSource dataSource,
        PostgreSqlOwnedWorldLocationStore locationStore,
        PostgreSqlOwnedWorldSnapshotStore snapshotStore,
        PostgreSqlOwnedWorldSnapshotRevisionEvidenceStore evidenceStore,
        UserIdentity owner,
        string installationId)
        => new(
            dataSource,
            locationStore,
            snapshotStore,
            evidenceStore,
            owner,
            installationId);

    private static async Task<SnapshotFixture> PublishLocationOnlyAsync(
        StoreContext context,
        WorldId? existingWorldId = null)
    {
        var fixture = new SnapshotFixture(
            existingWorldId ?? WorldId.New(),
            RevisionId.New(),
            RevisionId.New());
        var registry = new OwnedWorldLocationRegistry(context.LocationStore);
        var location = await registry.PublishLocationWithPresentationAsync(
            context.Owner,
            context.InstallationId,
            fixture.WorldId,
            fixture.StateId,
            fixture.EnvironmentId,
            "Factory World",
            "factorio",
            DateTimeOffset.UtcNow);
        Assert.Equal(OwnedWorldLocationWriteResult.Created, location.Result);
        return fixture;
    }

    private static async Task<SnapshotFixture> PublishSnapshotAsync(
        StoreContext context,
        WorldId? existingWorldId = null)
    {
        var fixture = await PublishLocationOnlyAsync(context, existingWorldId);
        var registry = new OwnedWorldSnapshotRegistry(
            context.LocationStore,
            context.SnapshotStore);
        var result = await registry.PublishAsync(
            context.Owner,
            context.InstallationId,
            fixture.WorldId,
            fixture.StateId,
            fixture.EnvironmentId,
            "factorio",
            $"private/{context.Owner.ExternalId}/{fixture.WorldId}/{fixture.StateId}",
            100,
            PackageSha256,
            Manifest(),
            CreatedAt.AddMinutes(1));
        Assert.Equal(OwnedWorldSnapshotWriteResult.Created, result.Result);
        return fixture;
    }

    private static OwnedWorldSnapshotRevisionEvidence Evidence(
        StoreContext context,
        SnapshotFixture fixture)
    {
        var manifest = Manifest();
        return new OwnedWorldSnapshotRevisionEvidence(
            context.Owner.Provider,
            context.Owner.ExternalId,
            context.InstallationId,
            fixture.WorldId,
            new StateRevision(
                fixture.StateId,
                fixture.WorldId,
                ParentRevisionId: RevisionId.New(),
                CreatedAt,
                context.Owner,
                "factorio",
                "state-package-id",
                fixture.EnvironmentId),
            new EnvironmentRevision(
                fixture.EnvironmentId,
                fixture.WorldId,
                ParentRevisionId: RevisionId.New(),
                CreatedAt.AddSeconds(-1),
                context.Owner,
                manifest),
            CreatedAt.AddMinutes(1).AddTicks(7));
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

    private static EnvironmentManifest Manifest()
        => new(
            SchemaVersion: 1,
            AdapterId: "factorio",
            GameVersion: "2.0.0",
            Components:
            [
                new EnvironmentComponent(
                    "mod",
                    "example",
                    "1.0.0",
                    "workshop",
                    new Dictionary<string, string>
                    {
                        ["hash"] = "abc"
                    })
            ],
            Configuration: new Dictionary<string, string>
            {
                ["difficulty"] = "normal"
            });

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
        => new(value.Ticks - (value.Ticks % 10), value.Offset);

    private sealed record StoreContext(
        NpgsqlDataSource DataSource,
        PostgreSqlOwnedWorldLocationStore LocationStore,
        PostgreSqlOwnedWorldSnapshotStore SnapshotStore,
        PostgreSqlOwnedWorldSnapshotRevisionEvidenceStore EvidenceStore,
        UserIdentity Owner,
        string InstallationId);

    private sealed record SnapshotFixture(
        WorldId WorldId,
        RevisionId StateId,
        RevisionId EnvironmentId);
}
