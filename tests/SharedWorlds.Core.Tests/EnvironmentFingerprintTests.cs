using SharedWorlds.Core.Environment;

namespace SharedWorlds.Core.Tests;

public sealed class EnvironmentFingerprintTests
{
    [Fact]
    public void Compute_IgnoresManifestCollectionOrdering()
    {
        var first = new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: "factorio",
            GameVersion: "2.0.0",
            Components:
            [
                new EnvironmentComponent("mod", "beta", "1.0.0", "user"),
                new EnvironmentComponent("mod", "alpha", "2.0.0", "user")
            ],
            Configuration: new Dictionary<string, string>
            {
                ["zeta"] = "1",
                ["alpha"] = "2"
            });

        var second = new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: "factorio",
            GameVersion: "2.0.0",
            Components:
            [
                new EnvironmentComponent("mod", "alpha", "2.0.0", "user"),
                new EnvironmentComponent("mod", "beta", "1.0.0", "user")
            ],
            Configuration: new Dictionary<string, string>
            {
                ["alpha"] = "2",
                ["zeta"] = "1"
            });

        Assert.Equal(
            EnvironmentFingerprint.Compute(first),
            EnvironmentFingerprint.Compute(second));
    }

    [Fact]
    public void Compute_ChangesWhenRelevantEnvironmentChanges()
    {
        var first = CreateManifest("1.0.0");
        var second = CreateManifest("1.0.1");

        Assert.NotEqual(
            EnvironmentFingerprint.Compute(first),
            EnvironmentFingerprint.Compute(second));
    }

    private static EnvironmentManifest CreateManifest(string modVersion)
        => new(
            SchemaVersion: 1,
            AdapterId: "factorio",
            GameVersion: "2.0.0",
            Components:
            [
                new EnvironmentComponent("mod", "example", modVersion, "user")
            ],
            Configuration: new Dictionary<string, string>());
}
