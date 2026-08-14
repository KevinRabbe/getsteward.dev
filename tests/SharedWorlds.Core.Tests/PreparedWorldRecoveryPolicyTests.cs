using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class PreparedWorldRecoveryPolicyTests
{
    private static readonly EnvironmentManifest Environment = new(
        SchemaVersion: 1,
        AdapterId: "factorio",
        GameVersion: "1",
        Components: [],
        Configuration: new Dictionary<string, string>(StringComparer.Ordinal));

    private static readonly GameInstallation Installation = new(
        "install",
        "game-root",
        "test");

    [Fact]
    public void ManagedLocationMustUseExactIdentityBoundRuntimePath()
    {
        var preparation = new PreparedWorldPreparationContext(
            WorkspaceId.New(),
            Path.GetFullPath(Path.Combine("managed", "factorio", "workspace")));
        var prepared = new PreparedWorld(
            Installation,
            preparation.ManagedWorkingDirectory,
            Environment,
            RecoveryLocation: PreparedWorldRecoveryLocation.Managed());

        var classification = PreparedWorldRecoveryPolicy.Classify(
            "factorio",
            preparation,
            prepared);

        Assert.Equal(
            PreparedWorldRecoveryClassification.SafeWorldManaged,
            classification);
    }

    [Fact]
    public void ManagedLocationCannotClaimDifferentRuntimePath()
    {
        var preparation = new PreparedWorldPreparationContext(
            WorkspaceId.New(),
            Path.GetFullPath(Path.Combine("managed", "factorio", "expected")));
        var prepared = new PreparedWorld(
            Installation,
            Path.GetFullPath(Path.Combine("managed", "factorio", "different")),
            Environment,
            RecoveryLocation: PreparedWorldRecoveryLocation.Managed());

        Assert.Throws<InvalidOperationException>(() =>
            PreparedWorldRecoveryPolicy.Classify("factorio", preparation, prepared));
    }

    [Fact]
    public void NativeLocationMayUseGameOwnedRuntimePath()
    {
        var preparation = new PreparedWorldPreparationContext(
            WorkspaceId.New(),
            Path.GetFullPath(Path.Combine("managed", "factorio", "unused")));
        var native = PreparedWorldRecoveryLocation.Native(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["worldId"] = "native-world"
            });
        var prepared = new PreparedWorld(
            Installation,
            Path.GetFullPath(Path.Combine("game-root", "native-world")),
            Environment,
            RecoveryLocation: native);

        var classification = PreparedWorldRecoveryPolicy.Classify(
            "factorio",
            preparation,
            prepared);

        Assert.Equal(PreparedWorldRecoveryClassification.NativeGame, classification);
    }

    [Fact]
    public void MissingDescriptorIsExplicitLegacyCompatibilityOnly()
    {
        var preparation = new PreparedWorldPreparationContext(
            WorkspaceId.New(),
            Path.GetFullPath(Path.Combine("managed", "factorio", "offered")));
        var prepared = new PreparedWorld(
            Installation,
            "legacy-absolute-runtime-path",
            Environment);

        var classification = PreparedWorldRecoveryPolicy.Classify(
            "factorio",
            preparation,
            prepared);

        Assert.Equal(
            PreparedWorldRecoveryClassification.LegacyAbsolutePath,
            classification);
    }
}
