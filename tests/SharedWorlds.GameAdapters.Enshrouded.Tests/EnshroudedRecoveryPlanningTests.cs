using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.Enshrouded.Tests;

public sealed class EnshroudedRecoveryPlanningTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "safeworld-enshrouded-recovery-planning",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PlannerIsSideEffectFreeAndMaterializationUsesExactCoreWorkspace()
    {
        var installation = CreateInstallation("24680");
        var environment = EnshroudedEnvironment.Inspect(installation);
        var adapter = new EnshroudedAdapter();
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
        var adapter = new EnshroudedAdapter();
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
        var installRoot = Path.Combine(library, "steamapps", "common", "Enshrouded");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "enshrouded.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_1203620.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"buildid\" \"{buildId}\" }}");
        var saveRoot = Path.Combine(_root, "save-games");

        return new GameInstallation(
            "enshrouded:test",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EnshroudedInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(
                    installRoot,
                    "enshrouded.exe"),
                [EnshroudedInstallationDiscovery.SteamManifestPathKey] = manifest,
                [EnshroudedInstallationDiscovery.SaveGamesRootPathKey] = saveRoot,
                [EnshroudedInstallationDiscovery.ModsRootPathKey] = Path.Combine(
                    installRoot,
                    "mods"),
                [EnshroudedInstallationDiscovery.GameSteamAppIdKey] =
                    EnshroudedInstallationDiscovery.GameSteamAppId
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
