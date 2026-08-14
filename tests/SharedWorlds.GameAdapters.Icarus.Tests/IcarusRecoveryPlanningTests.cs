using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.Icarus.Tests;

public sealed class IcarusRecoveryPlanningTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "safeworld-icarus-recovery-planning",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PlannerIsSideEffectFreeAndMaterializationUsesExactCoreWorkspace()
    {
        var installation = CreateInstallation("24680");
        var environment = IcarusEnvironment.Inspect(installation);
        var adapter = new IcarusAdapter();
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

        Assert.Equal(Path.GetFullPath(managedPath), Path.GetFullPath(prepared.WorkingDirectory));
        Assert.Equal(
            PreparedWorldRecoveryLocationKind.SafeWorldManaged,
            prepared.RecoveryLocation?.Kind);
    }

    [Fact]
    public async Task ManagedReleaseLeavesRootForCoreAndDirectDiscardIsRefused()
    {
        var workspace = Path.Combine(_root, "managed-release", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var adapter = new IcarusAdapter();
        var prepared = new PreparedWorld(
            new GameInstallation("test", _root, "test"),
            workspace,
            new EnvironmentManifest(
                1,
                adapter.Id,
                "1.0",
                [],
                new Dictionary<string, string>()),
            RecoveryLocation: PreparedWorldRecoveryLocation.Managed());

        await adapter.FinalizePreparedWorldAsync(
            prepared,
            PreparedWorldDisposition.ReleaseForCoreManagedDiscard);
        Assert.True(Directory.Exists(workspace));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.FinalizePreparedWorldAsync(
                prepared,
                PreparedWorldDisposition.Discard));
        Assert.Contains("Core owns", exception.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(workspace));
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var library = Path.Combine(_root, "library");
        var installRoot = Path.Combine(library, "steamapps", "common", "Icarus");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Icarus.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_1149460.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"appid\" \"1149460\" \"buildid\" \"{buildId}\" }}");
        var playerDataRoot = Path.Combine(_root, "player-data");

        return new GameInstallation(
            "icarus:test",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [IcarusInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(
                    installRoot,
                    "Icarus.exe"),
                [IcarusInstallationDiscovery.SteamManifestPathKey] = manifest,
                [IcarusInstallationDiscovery.PlayerDataRootPathKey] = playerDataRoot,
                [IcarusInstallationDiscovery.ModsRootPathKey] = Path.Combine(
                    installRoot,
                    "Icarus",
                    "Content",
                    "Paks",
                    "mods"),
                [IcarusInstallationDiscovery.GameSteamAppIdKey] =
                    IcarusInstallationDiscovery.GameSteamAppId
            });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Test cleanup only.
        }
    }
}
