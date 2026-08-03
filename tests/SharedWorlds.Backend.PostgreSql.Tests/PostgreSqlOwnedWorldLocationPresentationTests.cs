using Npgsql;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlOwnedWorldLocationPresentationTests
{
    [Fact]
    public async Task PresentationRoundTripsAndLegacyWritesCannotEraseIt()
    {
        var connectionString = Environment.GetEnvironmentVariable("STEWARD_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var store = new PostgreSqlOwnedWorldLocationStore(dataSource);
        await store.InitializeAsync();

        var suffix = Guid.NewGuid().ToString("N");
        var owner = new UserIdentity("steam", $"owner-{suffix}", "Owner");
        var installationId = $"pc-{suffix}";
        var registry = new OwnedWorldLocationRegistry(store);
        var observedAt = DateTimeOffset.UtcNow;
        await registry.RegisterInstallationAsync(
            owner,
            installationId,
            "Gaming PC",
            observedAt);

        var worldId = WorldId.New();
        var firstState = RevisionId.New();
        var firstEnvironment = RevisionId.New();
        var created = await registry.PublishLocationWithPresentationAsync(
            owner,
            installationId,
            worldId,
            firstState,
            firstEnvironment,
            "Factory World",
            "factorio",
            observedAt);
        Assert.Equal(OwnedWorldLocationWriteResult.Created, created.Result);

        var firstPersisted = Assert.Single(await store.ListWorldLocationsAsync(
            worldId,
            owner.Provider,
            owner.ExternalId));
        Assert.Equal(
            new OwnedWorldPresentation("Factory World", "factorio"),
            firstPersisted.Presentation);

        var renamed = await registry.PublishLocationWithPresentationAsync(
            owner,
            installationId,
            worldId,
            firstState,
            firstEnvironment,
            "Factory World Renamed",
            "factorio",
            observedAt.AddMinutes(1));
        Assert.Equal(OwnedWorldLocationWriteResult.NoChange, renamed.Result);
        Assert.Equal(
            "Factory World Renamed",
            renamed.Current!.Presentation!.Name);

        var legacyRepeat = await registry.PublishLocationAsync(
            owner,
            installationId,
            worldId,
            firstState,
            firstEnvironment,
            observedAt.AddMinutes(2));
        Assert.Equal(OwnedWorldLocationWriteResult.NoChange, legacyRepeat.Result);
        Assert.Equal(
            "Factory World Renamed",
            legacyRepeat.Current!.Presentation!.Name);

        var nextState = RevisionId.New();
        var nextEnvironment = RevisionId.New();
        var legacyUpdate = await registry.PublishLocationAsync(
            owner,
            installationId,
            worldId,
            nextState,
            nextEnvironment,
            observedAt.AddMinutes(3),
            expectedStateRevisionId: firstState,
            expectedEnvironmentRevisionId: firstEnvironment);
        Assert.Equal(OwnedWorldLocationWriteResult.Updated, legacyUpdate.Result);
        Assert.Equal(
            "Factory World Renamed",
            legacyUpdate.Current!.Presentation!.Name);

        var finalPersisted = Assert.Single(await store.ListWorldLocationsAsync(
            worldId,
            owner.Provider,
            owner.ExternalId));
        Assert.Equal(nextState, finalPersisted.StateRevisionId);
        Assert.Equal(nextEnvironment, finalPersisted.EnvironmentRevisionId);
        Assert.Equal(
            new OwnedWorldPresentation("Factory World Renamed", "factorio"),
            finalPersisted.Presentation);
    }

    [Fact]
    public void SchemaMigrationKeepsPresentationOptionalButPaired()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Backend.PostgreSql/PostgreSqlOwnedWorldLocationStore.cs"));

        Assert.Contains(
            "ADD COLUMN IF NOT EXISTS world_name text NULL",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ADD COLUMN IF NOT EXISTS game_adapter_id text NULL",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CHECK ((world_name IS NULL) = (game_adapter_id IS NULL))",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Presentation = desired.Presentation ?? current.Presentation",
            source,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var candidate = Path.Combine(workspace, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }
}
