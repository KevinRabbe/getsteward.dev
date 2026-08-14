using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.GameAdapters.VRising.Tests;

public sealed class VRisingRecoveryPlanningTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "safeworld-v-rising-recovery-planning",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PlannerIsSideEffectFreeAndMaterializationUsesExactCoreWorkspace()
    {
        var installation = CreateInstallation("24680");
        var environment = VRisingEnvironment.Inspect(installation);
        var adapter = new VRisingAdapter();
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
        var installName = "VRising";
        var installRoot = Path.Combine(library, "steamapps", "common", installName);
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "VRising.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_1604030.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"appid\" \"1604030\" \"buildid\" \"{buildId}\" \"installdir\" \"{installName}\" }}");
        var saveRoot = Path.Combine(_root, "saves", "v4");
        Directory.CreateDirectory(saveRoot);

        return new GameInstallation(
            "v-rising:test",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [VRisingInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(
                    installRoot,
                    "VRising.exe"),
                [VRisingInstallationDiscovery.SteamManifestPathKey] = manifest,
                [VRisingInstallationDiscovery.SaveVersionRootPathKey] = saveRoot,
                [VRisingInstallationDiscovery.GameSteamAppIdKey] =
                    VRisingInstallationDiscovery.GameSteamAppId
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
