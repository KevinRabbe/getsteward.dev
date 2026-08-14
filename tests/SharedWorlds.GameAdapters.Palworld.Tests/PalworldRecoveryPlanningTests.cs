using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Palworld.Tests;

public sealed class PalworldRecoveryPlanningTests
{
    [Fact]
    public async Task PlannerReturnsStableNativeWorldIdentityWithoutUsingManagedPath()
    {
        var adapter = new PalworldAdapter();
        var planner = Assert.IsAssignableFrom<IPreparedWorldRecoveryPlanner>(adapter);
        var workspaceId = WorkspaceId.New();
        var managedPath = Path.Combine(
            Path.GetTempPath(),
            "SafeWorld",
            "workspaces",
            "palworld",
            workspaceId.ToString());
        var environment = CreateEnvironment("ABCDEF0123456789");

        var location = await planner.PlanPreparedWorldRecoveryAsync(
            new GameInstallation("test", Path.GetTempPath(), "test"),
            environment,
            new PreparedWorldPreparationContext(workspaceId, managedPath));

        Assert.Equal(PreparedWorldRecoveryLocationKind.NativeGame, location.Kind);
        Assert.NotNull(location.NativeIdentity);
        Assert.Equal("ABCDEF0123456789", location.NativeIdentity!["worldId"]);
        Assert.DoesNotContain(managedPath, location.NativeIdentity.Values);
        Assert.DoesNotContain(
            location.NativeIdentity.Values,
            static value => Path.IsPathRooted(value));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("a/b")]
    public async Task PlannerRejectsPathLikeWorldIdentity(string worldId)
    {
        var adapter = new PalworldAdapter();
        var planner = Assert.IsAssignableFrom<IPreparedWorldRecoveryPlanner>(adapter);
        var workspaceId = WorkspaceId.New();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            planner.PlanPreparedWorldRecoveryAsync(
                new GameInstallation("test", Path.GetTempPath(), "test"),
                CreateEnvironment(worldId),
                new PreparedWorldPreparationContext(
                    workspaceId,
                    Path.Combine(Path.GetTempPath(), workspaceId.ToString()))));
    }

    private static EnvironmentManifest CreateEnvironment(string worldId)
        => new(
            SchemaVersion: 1,
            AdapterId: "palworld",
            GameVersion: "test-build",
            Components: [],
            Configuration: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["hostingMode"] = "dedicated-server",
                ["dedicatedServerName"] = worldId
            });
}
