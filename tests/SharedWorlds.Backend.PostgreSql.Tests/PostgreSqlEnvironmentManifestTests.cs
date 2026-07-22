using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlEnvironmentManifestTests : IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlSharedWorldStore _store = null!;
    private SharedRevisionMetadataService _revisions = null!;
    private VerifiedExternalIdentity _manager = null!;
    private WorldId _worldId;
    private RevisionId _environmentId;

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

        _store = new PostgreSqlSharedWorldStore(_dataSource);
        var worlds = new SharedWorldMetadataService(
            _store,
            () => new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero));
        _revisions = new SharedRevisionMetadataService(_store, _store);
        _manager = new VerifiedExternalIdentity(
            new ExternalIdentityRef("steam", "76561198000000001"));
        _worldId = WorldId.New();
        _environmentId = RevisionId.New();

        var created = await worlds.CreateSharedWorldAsync(
            _manager,
            new CreateSharedWorldCommand(
                _worldId,
                "factorio",
                "Manifest World",
                RevisionId.New(),
                _environmentId));
        Assert.Equal(CreateSharedWorldStatus.Created, created.Status);
    }

    public async Task DisposeAsync() => await _dataSource.DisposeAsync();

    [Fact]
    public async Task ManifestRoundTripsAndSameLogicalManifestIsIdempotent()
    {
        var manifest = Manifest(
            new Dictionary<string, string>
            {
                ["difficulty"] = "normal",
                ["seed-policy"] = "fixed"
            });

        Assert.Equal(
            PublishEnvironmentManifestStatus.Published,
            await _revisions.PublishEnvironmentManifestAsync(
                _manager,
                _worldId,
                _environmentId,
                manifest));

        var loaded = Assert.IsType<SharedEnvironmentRevisionMetadata>(
            await _store.LoadEnvironmentRevisionAsync(_worldId, _environmentId));
        Assert.Equal("factorio", loaded.AdapterId);
        Assert.Equal($"manifest:{_environmentId.Value:N}", loaded.ArtifactReference);
        Assert.Null(loaded.ByteSize);
        Assert.Null(loaded.Sha256);
        Assert.NotNull(loaded.Manifest);
        Assert.Equal(manifest.SchemaVersion, loaded.Manifest.SchemaVersion);
        Assert.Equal(manifest.GameVersion, loaded.Manifest.GameVersion);
        Assert.Equal(manifest.Components, loaded.Manifest.Components);
        Assert.Equal("normal", loaded.Manifest.Configuration["difficulty"]);

        var reordered = Manifest(
            new Dictionary<string, string>
            {
                ["seed-policy"] = "fixed",
                ["difficulty"] = "normal"
            });
        Assert.Equal(
            PublishEnvironmentManifestStatus.AlreadyPublished,
            await _revisions.PublishEnvironmentManifestAsync(
                _manager,
                _worldId,
                _environmentId,
                reordered));
    }

    [Fact]
    public async Task SameRevisionIdWithDifferentManifestConflicts()
    {
        Assert.Equal(
            PublishEnvironmentManifestStatus.Published,
            await _revisions.PublishEnvironmentManifestAsync(
                _manager,
                _worldId,
                _environmentId,
                Manifest(new Dictionary<string, string> { ["difficulty"] = "normal" })));

        Assert.Equal(
            PublishEnvironmentManifestStatus.Conflict,
            await _revisions.PublishEnvironmentManifestAsync(
                _manager,
                _worldId,
                _environmentId,
                Manifest(new Dictionary<string, string> { ["difficulty"] = "hard" })));
    }

    [Fact]
    public async Task NonMemberCannotPublishAndAdapterMismatchIsRejected()
    {
        var outsider = new VerifiedExternalIdentity(
            new ExternalIdentityRef("steam", "76561198000000002"));
        Assert.Equal(
            PublishEnvironmentManifestStatus.NotFoundOrUnauthorized,
            await _revisions.PublishEnvironmentManifestAsync(
                outsider,
                _worldId,
                _environmentId,
                Manifest([])));

        var mismatched = Manifest([]) with { AdapterId = "palworld" };
        Assert.Equal(
            PublishEnvironmentManifestStatus.AdapterMismatch,
            await _revisions.PublishEnvironmentManifestAsync(
                _manager,
                _worldId,
                _environmentId,
                mismatched));
    }

    private static EnvironmentManifest Manifest(IReadOnlyDictionary<string, string> configuration)
        => new(
            SchemaVersion: 1,
            AdapterId: "factorio",
            GameVersion: "2.0.0",
            Components:
            [
                new EnvironmentComponent(
                    "mod",
                    "base",
                    "2.0.0",
                    "steam",
                    new Dictionary<string, string> { ["required"] = "true" })
            ],
            Configuration: configuration);

    private async Task ResetAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "TRUNCATE TABLE steward_authority_idempotency, steward_object_cleanup_queue, " +
            "steward_world_reservations, steward_package_transfers, steward_world_invitations, " +
            "steward_state_revisions, steward_environment_revisions, steward_world_members, " +
            "steward_shared_worlds CASCADE;");
        await command.ExecuteNonQueryAsync();
    }
}
