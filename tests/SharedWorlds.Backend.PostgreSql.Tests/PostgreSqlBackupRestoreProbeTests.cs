using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlBackupRestoreProbeTests
{
    private const string ProbeModeEnvironmentVariable = "STEWARD_BACKUP_RESTORE_MODE";
    private const string Hash = "ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB";

    private static readonly WorldId WorldId = new(Guid.Parse("7f76f495-d7bf-4cae-bda0-1299fd773101"));
    private static readonly RevisionId StateRevisionId = new(Guid.Parse("7f76f495-d7bf-4cae-bda0-1299fd773102"));
    private static readonly RevisionId EnvironmentRevisionId = new(Guid.Parse("7f76f495-d7bf-4cae-bda0-1299fd773103"));
    private static readonly VerifiedExternalIdentity Manager = new(
        new ExternalIdentityRef("steam", "76561198999990101"),
        "Backup Probe Manager");
    private static readonly DateTimeOffset PublishedAt =
        new(2026, 7, 24, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProviderNativeBackupRestoreRoundTrip()
    {
        var mode = Environment.GetEnvironmentVariable(ProbeModeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(mode))
        {
            // The ordinary PostgreSQL integration suite should not mutate durable probe state.
            // CI invokes this test explicitly in seed and verify modes around pg_dump/pg_restore.
            return;
        }

        var connectionString = Environment.GetEnvironmentVariable("STEWARD_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "STEWARD_TEST_POSTGRES must be set for the PostgreSQL backup/restore probe.");
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        switch (mode)
        {
            case "seed":
                await SeedAsync(dataSource);
                break;
            case "verify":
                await VerifyAsync(dataSource);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown {ProbeModeEnvironmentVariable} value '{mode}'.");
        }
    }

    private static async Task SeedAsync(NpgsqlDataSource dataSource)
    {
        await PostgreSqlBackendSchema.InitializeAsync(dataSource);
        var store = new PostgreSqlSharedWorldStore(dataSource);
        var worlds = new SharedWorldMetadataService(store, () => PublishedAt);
        var revisions = new SharedRevisionMetadataService(store, store);

        var create = await worlds.CreateSharedWorldAsync(
            Manager,
            new CreateSharedWorldCommand(
                WorldId,
                "factorio",
                "PostgreSQL Backup Probe",
                StateRevisionId,
                EnvironmentRevisionId));
        Assert.Equal(CreateSharedWorldStatus.Created, create.Status);

        var manifest = CreateManifest();
        var environment = new SharedEnvironmentRevisionMetadata(
            WorldId,
            EnvironmentRevisionId,
            "factorio",
            "backup-probe/environment",
            4096,
            Hash,
            Manager.Subject,
            PublishedAt,
            manifest);
        Assert.Equal(
            RecordRevisionMetadataStatus.Recorded,
            await revisions.RecordVerifiedEnvironmentRevisionAsync(environment));

        var state = new SharedStateRevisionMetadata(
            WorldId,
            StateRevisionId,
            "factorio",
            "backup-probe/state",
            8192,
            Hash,
            EnvironmentRevisionId,
            Manager.Subject,
            PublishedAt.AddSeconds(1));
        Assert.Equal(
            RecordRevisionMetadataStatus.Recorded,
            await revisions.RecordVerifiedStateRevisionAsync(state));
    }

    private static async Task VerifyAsync(NpgsqlDataSource dataSource)
    {
        // Deliberately do not call PostgreSqlBackendSchema.InitializeAsync here. A successful
        // verification must prove pg_restore recreated the real schema rather than letting the
        // application repair missing tables before the assertion.
        var store = new PostgreSqlSharedWorldStore(dataSource);
        var revisions = new SharedRevisionMetadataService(store, store);

        var world = Assert.IsType<SharedWorldMetadata>(await store.LoadWorldAsync(WorldId));
        Assert.Equal("factorio", world.AdapterId);
        Assert.Equal("PostgreSQL Backup Probe", world.DisplayName);
        Assert.Equal(StateRevisionId, world.CurrentStateRevisionId);
        Assert.Equal(EnvironmentRevisionId, world.CurrentEnvironmentRevisionId);
        Assert.Equal(Manager.Subject, world.AccessManager);

        var manager = Assert.IsType<SharedWorldMember>(
            await store.LoadMemberAsync(WorldId, Manager.Subject));
        Assert.Equal(SharedWorldMemberStatus.Active, manager.Status);

        var environment = Assert.IsType<SharedEnvironmentRevisionMetadata>(
            await revisions.GetEnvironmentRevisionMetadataAsync(
                Manager,
                WorldId,
                EnvironmentRevisionId));
        Assert.Equal("backup-probe/environment", environment.ArtifactReference);
        Assert.Equal(Hash, environment.Sha256);
        Assert.Equal(CreateManifest(), environment.Manifest);

        var state = Assert.IsType<SharedStateRevisionMetadata>(
            await revisions.GetStateRevisionMetadataAsync(
                Manager,
                WorldId,
                StateRevisionId));
        Assert.Equal("backup-probe/state", state.PackageObjectKey);
        Assert.Equal(Hash, state.Sha256);
        Assert.Equal(EnvironmentRevisionId, state.RequiredEnvironmentRevisionId);
        Assert.Equal(Manager.Subject, state.PublishedBy);
    }

    private static EnvironmentManifest CreateManifest()
        => new(
            1,
            "factorio",
            "2.1.11",
            [new EnvironmentComponent("mod", "base", "2.1.11", Hash)],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["startupSettingsSha256"] = Hash
            });
}
