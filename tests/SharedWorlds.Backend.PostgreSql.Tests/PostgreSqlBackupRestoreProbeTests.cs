using System.Diagnostics;
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
    private const string Hash = "ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB";
    private const string PostgreSqlImage = "postgres:17-alpine";

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
        var connectionString = Environment.GetEnvironmentVariable("STEWARD_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString) ||
            !string.Equals(
                Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            // The normal unit matrix and local PostgreSQL development runs do not require Docker.
            // The real provider-native proof executes in the GitHub PostgreSQL integration job.
            return;
        }

        var source = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(source.Host) ||
            string.IsNullOrWhiteSpace(source.Username) ||
            string.IsNullOrWhiteSpace(source.Database))
        {
            throw new InvalidOperationException(
                "STEWARD_TEST_POSTGRES must identify a host, username, and database.");
        }

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"steward-postgres-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        var dumpPath = Path.Combine(temporaryDirectory, "steward.dump");
        var restoreDatabase = $"steward_restore_{Guid.NewGuid():N}";
        var restoreCreated = false;

        try
        {
            await using (var sourceDataSource = NpgsqlDataSource.Create(source.ConnectionString))
            {
                await SeedAsync(sourceDataSource);
            }

            await RunPostgreSqlContainerAsync(
                source,
                temporaryDirectory,
                [
                    "pg_dump",
                    "--host", source.Host,
                    "--port", source.Port.ToString(),
                    "--username", source.Username,
                    "--dbname", source.Database,
                    "--format", "custom",
                    "--no-owner",
                    "--no-acl",
                    "--file", "/backup/steward.dump"
                ]);

            Assert.True(File.Exists(dumpPath), "pg_dump did not create the expected backup file.");
            Assert.True(new FileInfo(dumpPath).Length > 0, "pg_dump created an empty backup file.");

            await RunPostgreSqlContainerAsync(
                source,
                temporaryDirectory,
                [
                    "psql",
                    "--host", source.Host,
                    "--port", source.Port.ToString(),
                    "--username", source.Username,
                    "--dbname", "postgres",
                    "--set", "ON_ERROR_STOP=1",
                    "--command", $"CREATE DATABASE \"{restoreDatabase}\";"
                ]);
            restoreCreated = true;

            await RunPostgreSqlContainerAsync(
                source,
                temporaryDirectory,
                [
                    "pg_restore",
                    "--host", source.Host,
                    "--port", source.Port.ToString(),
                    "--username", source.Username,
                    "--dbname", restoreDatabase,
                    "--exit-on-error",
                    "--no-owner",
                    "--no-acl",
                    "/backup/steward.dump"
                ]);

            var restored = new NpgsqlConnectionStringBuilder(source.ConnectionString)
            {
                Database = restoreDatabase,
                Pooling = false
            };
            await using (var restoredDataSource = NpgsqlDataSource.Create(restored.ConnectionString))
            {
                await VerifyAsync(restoredDataSource);
            }
        }
        finally
        {
            if (restoreCreated)
            {
                try
                {
                    await RunPostgreSqlContainerAsync(
                        source,
                        temporaryDirectory,
                        [
                            "psql",
                            "--host", source.Host,
                            "--port", source.Port.ToString(),
                            "--username", source.Username,
                            "--dbname", "postgres",
                            "--set", "ON_ERROR_STOP=1",
                            "--command", $"DROP DATABASE IF EXISTS \"{restoreDatabase}\" WITH (FORCE);"
                        ]);
                }
                catch
                {
                    // Do not hide the actual backup/restore assertion failure with cleanup noise.
                }
            }

            try
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
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

        var environment = new SharedEnvironmentRevisionMetadata(
            WorldId,
            EnvironmentRevisionId,
            "factorio",
            "backup-probe/environment",
            4096,
            Hash,
            Manager.Subject,
            PublishedAt,
            CreateManifest());
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
        // Deliberately do not call PostgreSqlBackendSchema.InitializeAsync here. Successful reads
        // must prove pg_restore recreated the schema rather than letting Steward repair it first.
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
        var manifest = Assert.IsType<EnvironmentManifest>(environment.Manifest);
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("factorio", manifest.AdapterId);
        Assert.Equal("2.1.11", manifest.GameVersion);
        var component = Assert.Single(manifest.Components);
        Assert.Equal("mod", component.Kind);
        Assert.Equal("base", component.Id);
        Assert.Equal("2.1.11", component.Version);
        Assert.Equal(Hash, component.Hash);
        Assert.Equal(Hash, manifest.Configuration["startupSettingsSha256"]);

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

    private static async Task RunPostgreSqlContainerAsync(
        NpgsqlConnectionStringBuilder connection,
        string backupDirectory,
        IReadOnlyList<string> postgresArguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("--rm");
        process.StartInfo.ArgumentList.Add("--network");
        process.StartInfo.ArgumentList.Add("host");
        process.StartInfo.ArgumentList.Add("-e");
        process.StartInfo.ArgumentList.Add("PGPASSWORD");
        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add($"{Path.GetFullPath(backupDirectory)}:/backup");
        process.StartInfo.ArgumentList.Add(PostgreSqlImage);
        foreach (var argument in postgresArguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.Environment["PGPASSWORD"] = connection.Password ?? string.Empty;
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start PostgreSQL backup/restore container.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"PostgreSQL backup/restore command failed with exit code {process.ExitCode}. " +
                $"stdout: {output.Trim()} stderr: {error.Trim()}");
        }
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
