using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class PreparedWorldRecoveryPlanPolicyTests
{
    [Fact]
    public void ManagedPlanRequiresExactOfferedRuntimePath()
    {
        var preparation = CreatePreparation();
        var plan = PreparedWorldRecoveryLocation.Managed();
        var prepared = CreatePrepared(
            preparation.ManagedWorkingDirectory,
            plan);

        var classification = PreparedWorldRecoveryPlanPolicy.ValidateMaterializedResult(
            "test-adapter",
            preparation,
            plan,
            prepared);

        Assert.Equal(PreparedWorldRecoveryClassification.SafeWorldManaged, classification);
    }

    [Fact]
    public void ManagedPlanRejectsDifferentRuntimePath()
    {
        var preparation = CreatePreparation();
        var plan = PreparedWorldRecoveryLocation.Managed();
        var prepared = CreatePrepared(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            plan);

        Assert.Throws<InvalidOperationException>(() =>
            PreparedWorldRecoveryPlanPolicy.ValidateMaterializedResult(
                "test-adapter",
                preparation,
                plan,
                prepared));
    }

    [Fact]
    public void NativePlanRequiresExactStableIdentity()
    {
        var preparation = CreatePreparation();
        var plan = PreparedWorldRecoveryLocation.Native(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["worldId"] = "A"
            });
        var changed = PreparedWorldRecoveryLocation.Native(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["worldId"] = "B"
            });

        Assert.Throws<InvalidOperationException>(() =>
            PreparedWorldRecoveryPlanPolicy.ValidateMaterializedResult(
                "test-adapter",
                preparation,
                plan,
                CreatePrepared(Path.GetTempPath(), changed)));
    }

    [Fact]
    public void PlannedAdapterCannotDropRecoveryIdentityAfterMaterialization()
    {
        var preparation = CreatePreparation();
        var plan = PreparedWorldRecoveryLocation.Native(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["worldId"] = "A"
            });

        Assert.Throws<InvalidOperationException>(() =>
            PreparedWorldRecoveryPlanPolicy.ValidateMaterializedResult(
                "test-adapter",
                preparation,
                plan,
                CreatePrepared(Path.GetTempPath(), recoveryLocation: null)));
    }

    private static PreparedWorldPreparationContext CreatePreparation()
    {
        var workspaceId = WorkspaceId.New();
        return new PreparedWorldPreparationContext(
            workspaceId,
            Path.Combine(Path.GetTempPath(), "SafeWorld", "workspaces", workspaceId.ToString()));
    }

    private static PreparedWorld CreatePrepared(
        string path,
        PreparedWorldRecoveryLocation? recoveryLocation)
        => new(
            new GameInstallation("test", Path.GetTempPath(), "test"),
            path,
            new EnvironmentManifest(
                1,
                "test-adapter",
                "1",
                [],
                new Dictionary<string, string>()),
            RecoveryLocation: recoveryLocation);
}
