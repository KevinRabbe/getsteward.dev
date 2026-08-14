using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.GameAdapters.Raft.Tests;

public sealed class RaftRecoveryPlanningTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "safeworld-raft-recovery-planning",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PlannerIsSideEffectFreeAndMaterializationUsesExactCoreWorkspace()
    {
        var installation = CreateInstallation("24680");
        var environment = RaftEnvironment.Inspect(installation);
        var adapter = new RaftAdapter();
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
        var installRoot = Path.Combine(library, "steamapps", "common", "Raft");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Raft.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_648800.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"appid\" \"648800\" \"buildid\" \"{buildId}\" }}");
        var userRoot = Path.Combine(_root, "user-root");
        var rmlRoot = Path.Combine(_root, "rml");

        return new GameInstallation(
            "raft:test",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RaftInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(
                    installRoot,
                    "Raft.exe"),
                [RaftInstallationDiscovery.SteamManifestPathKey] = manifest,
                [RaftInstallationDiscovery.UserRootPathKey] = userRoot,
                [RaftInstallationDiscovery.ModsRootPathKey] = Path.Combine(
                    installRoot,
                    "mods"),
                [RaftInstallationDiscovery.RmlRootPathKey] = rmlRoot,
                [RaftInstallationDiscovery.GameSteamAppIdKey] =
                    RaftInstallationDiscovery.GameSteamAppId
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
