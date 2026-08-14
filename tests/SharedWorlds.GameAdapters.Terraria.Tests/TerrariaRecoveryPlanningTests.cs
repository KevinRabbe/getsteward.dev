using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.GameAdapters.Terraria.Tests;

public sealed class TerrariaRecoveryPlanningTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "safeworld-terraria-recovery-planning",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PlannerIsSideEffectFreeAndMaterializationUsesExactCoreWorkspace()
    {
        var installation = CreateInstallation("24680");
        var environment = TerrariaEnvironment.Inspect(installation);
        var adapter = new TerrariaAdapter();
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
        var installRoot = Path.Combine(_root, "install");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Terraria.exe"), [1]);
        var manifest = Path.Combine(_root, "appmanifest.acf");
        File.WriteAllText(manifest, $"\"AppState\" {{ \"buildid\" \"{buildId}\" }}");
        var worldRoot = Path.Combine(_root, "worlds");

        return new GameInstallation(
            "terraria:test",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TerrariaInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(installRoot, "Terraria.exe"),
                [TerrariaInstallationDiscovery.SteamManifestPathKey] = manifest,
                [TerrariaInstallationDiscovery.UserDataPathKey] = worldRoot,
                [TerrariaInstallationDiscovery.GameSteamAppIdKey] = TerrariaInstallationDiscovery.GameSteamAppId
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
