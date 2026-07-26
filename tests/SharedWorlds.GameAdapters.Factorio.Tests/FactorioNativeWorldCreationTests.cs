using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Factorio.Tests;

public sealed class FactorioNativeWorldCreationTests
{
    [Fact]
    public void AdapterAdvertisesNativeCreationThroughGenericBoundary()
    {
        IGameAdapter adapter = new FactorioAdapter();

        Assert.True(adapter.Capabilities.HasFlag(GameAdapterCapabilities.NativeWorldCreation));
    }

    [Fact]
    public void DefaultCreationUsesFactorioCreateWithoutInventingSettings()
    {
        var arguments = FactorioWorldOperations.BuildNativeCreateArguments(
            Path.Combine(Path.GetTempPath(), "world.zip"),
            new WorldCreationRequest("Our Factory"));

        Assert.Equal("--create", arguments[0]);
        Assert.EndsWith("world.zip", arguments[1], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, arguments.Count);
    }

    [Fact]
    public void PresetAndSeedMapDirectlyToSupportedFactorioCliArguments()
    {
        var arguments = FactorioWorldOperations.BuildNativeCreateArguments(
            Path.Combine(Path.GetTempPath(), "world.zip"),
            new WorldCreationRequest(
                "Our Factory",
                new Dictionary<string, string>
                {
                    [FactorioWorldOperations.CreationPresetSetting] = "rail-world",
                    [FactorioWorldOperations.CreationSeedSetting] = "4294967295"
                }));

        Assert.Equal(
            [
                "--create",
                Path.GetFullPath(Path.Combine(Path.GetTempPath(), "world.zip")),
                "--preset",
                "rail-world",
                "--map-gen-seed",
                "4294967295"
            ],
            arguments);
    }

    [Theory]
    [InlineData("seed", "-1")]
    [InlineData("seed", "4294967296")]
    [InlineData("seed", "abc")]
    [InlineData("preset", "  rail-world")]
    [InlineData("preset", "rail-world\n")]
    [InlineData("unknown", "value")]
    public void InvalidOrUnsupportedCreationSettingsFailBeforeLaunchingFactorio(
        string key,
        string value)
    {
        var request = new WorldCreationRequest(
            "Our Factory",
            new Dictionary<string, string> { [key] = value });

        Assert.Throws<ArgumentException>(() =>
            FactorioWorldOperations.BuildNativeCreateArguments(
                Path.Combine(Path.GetTempPath(), "world.zip"),
                request));
    }
}
