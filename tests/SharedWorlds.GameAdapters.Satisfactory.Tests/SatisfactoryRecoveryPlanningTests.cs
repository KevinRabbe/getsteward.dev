using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.GameAdapters.Satisfactory.Tests;

public sealed class SatisfactoryRecoveryPlanningTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "safeworld-satisfactory-recovery-planning",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PlannerIsSideEffectFreeAndMaterializationUsesExactCoreWorkspace()
    {
        var installation = CreateInstallation("24680");
        var environment = SatisfactoryEnvironment.Inspect(installation);
        var adapter = new SatisfactoryAdapter();
        var planner = Assert.IsAssignableFrom<IPreparedWorldRecoveryPlanner>(adapter);
        var workspaceId = WorkspaceId.New();
        var managedPath = Path.Combine(_root, "managed", workspaceId.ToString());
        var preparation = new PreparedWorldPreparationContext(workspaceId, managedPath);

        var location = await planner.PlanPreparedWorldRecoveryAsync(
            installation,
            environment,
            preparation);

        Assert.Equal(PreparedWorldRecoveryLocationKind.SafeWorldManaged, location.Kind);
        Assert.False(Directory.Exists(managedPath));

        Directory.CreateDirectory(managedPath);
        var prepared = await adapter.PrepareEnvironmentAsync(
            installation,
            environment,
            preparation);
        try
        {
            Assert.Equal(Path.GetFullPath(managedPath), Path.GetFullPath(prepared.WorkingDirectory));
            Assert.Equal(
                PreparedWorldRecoveryLocationKind.SafeWorldManaged,
                prepared.RecoveryLocation?.Kind);
        }
        finally
        {
            await adapter.FinalizePreparedWorldAsync(
                prepared,
                PreparedWorldDisposition.Discard);
        }
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var library = Path.Combine(_root, "library");
        var installRoot = Path.Combine(library, "steamapps", "common", "Satisfactory");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "FactoryGameSteam.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_526870.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"buildid\" \"{buildId}\" }}");
        var saveGamesRoot = Path.Combine(_root, "save-games");

        return new GameInstallation(
            "satisfactory:test",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SatisfactoryInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(
                    installRoot,
                    "FactoryGameSteam.exe"),
                [SatisfactoryInstallationDiscovery.SteamManifestPathKey] = manifest,
                [SatisfactoryInstallationDiscovery.SaveGamesRootPathKey] = saveGamesRoot,
                [SatisfactoryInstallationDiscovery.ModsRootPathKey] = Path.Combine(
                    installRoot,
                    "FactoryGame",
                    "Mods"),
                [SatisfactoryInstallationDiscovery.WorkshopContentRootPathKey] = Path.Combine(
                    library,
                    "steamapps",
                    "workshop",
                    "content",
                    "526870"),
                [SatisfactoryInstallationDiscovery.GameSteamAppIdKey] =
                    SatisfactoryInstallationDiscovery.GameSteamAppId
            });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
