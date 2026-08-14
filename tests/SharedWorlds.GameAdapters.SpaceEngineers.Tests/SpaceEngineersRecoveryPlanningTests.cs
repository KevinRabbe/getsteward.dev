using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.SpaceEngineers.Tests;

public sealed class SpaceEngineersRecoveryPlanningTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "safeworld-space-engineers-recovery-planning",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PlannerIsSideEffectFreeAndMaterializationUsesExactCoreWorkspace()
    {
        var installation = CreateInstallation("24680");
        var environment = new EnvironmentManifest(
            1,
            "space-engineers",
            "24680",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var adapter = new SpaceEngineersAdapter();
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
        var installRoot = Path.Combine(library, "steamapps", "common", "SpaceEngineers");
        Directory.CreateDirectory(Path.Combine(installRoot, "Bin64"));
        File.WriteAllBytes(
            Path.Combine(installRoot, "Bin64", "SpaceEngineers.exe"),
            [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_244850.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"buildid\" \"{buildId}\" }}");
        var saveRoot = Path.Combine(_root, "saves");

        return new GameInstallation(
            "space-engineers:test",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SpaceEngineersInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(
                    installRoot,
                    "Bin64",
                    "SpaceEngineers.exe"),
                [SpaceEngineersInstallationDiscovery.SteamManifestPathKey] = manifest,
                [SpaceEngineersInstallationDiscovery.SaveProfilesRootPathKey] = saveRoot,
                [SpaceEngineersInstallationDiscovery.GameSteamAppIdKey] =
                    SpaceEngineersInstallationDiscovery.GameSteamAppId
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
