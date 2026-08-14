using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Factorio.Tests;

public sealed class FactorioRecoveryPlanningTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "safeworld-factorio-recovery-planning",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PlannerReturnsManagedIdentityWithoutCreatingOfferedWorkspace()
    {
        var adapter = new FactorioAdapter();
        var planner = Assert.IsAssignableFrom<IPreparedWorldRecoveryPlanner>(adapter);
        var workspaceId = WorkspaceId.New();
        var offered = Path.Combine(_root, "managed", "factorio", workspaceId.ToString());
        var environment = new EnvironmentManifest(
            1,
            adapter.Id,
            "1.0",
            [],
            new Dictionary<string, string>());

        var location = await planner.PlanPreparedWorldRecoveryAsync(
            new GameInstallation("test", _root, "test"),
            environment,
            new PreparedWorldPreparationContext(workspaceId, offered));

        Assert.Equal(PreparedWorldRecoveryLocationKind.SafeWorldManaged, location.Kind);
        Assert.False(Directory.Exists(offered));
    }

    [Fact]
    public void ManagedPreparedWorldUsesDescriptorInsteadOfApplicationRootHeuristic()
    {
        var prepared = CreateManagedPreparedWorld();

        FactorioWorkspaceOwnership.RequireOwned(prepared);
    }

    [Fact]
    public async Task ManagedReleaseLeavesRootForCoreDeletion()
    {
        var prepared = CreateManagedPreparedWorld();
        var adapter = new FactorioAdapter();

        await adapter.FinalizePreparedWorldAsync(
            prepared,
            PreparedWorldDisposition.ReleaseForCoreManagedDiscard);

        Assert.True(Directory.Exists(prepared.WorkingDirectory));
    }

    [Fact]
    public async Task DirectManagedDiscardIsRefusedAndPreservesRoot()
    {
        var prepared = CreateManagedPreparedWorld();
        var adapter = new FactorioAdapter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.FinalizePreparedWorldAsync(
                prepared,
                PreparedWorldDisposition.Discard));

        Assert.Contains("Core owns", exception.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(prepared.WorkingDirectory));
    }

    [Fact]
    public void NativeRecoveryDescriptorIsNotAcceptedAsFactorioManagedWorkspace()
    {
        var workspace = Path.Combine(_root, "native", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var environment = new EnvironmentManifest(
            1,
            "factorio",
            "1.0",
            [],
            new Dictionary<string, string>());
        var prepared = new PreparedWorld(
            new GameInstallation("test", _root, "test"),
            workspace,
            environment,
            RecoveryLocation: PreparedWorldRecoveryLocation.Native(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["worldId"] = "not-factorio-managed"
                }));

        Assert.Throws<InvalidOperationException>(() =>
            FactorioWorkspaceOwnership.RequireOwned(prepared));
    }

    private PreparedWorld CreateManagedPreparedWorld()
    {
        var workspace = Path.Combine(
            _root,
            "arbitrary-managed-root",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var environment = new EnvironmentManifest(
            1,
            "factorio",
            "1.0",
            [],
            new Dictionary<string, string>());
        return new PreparedWorld(
            new GameInstallation("test", _root, "test"),
            workspace,
            environment,
            RecoveryLocation: PreparedWorldRecoveryLocation.Managed());
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
