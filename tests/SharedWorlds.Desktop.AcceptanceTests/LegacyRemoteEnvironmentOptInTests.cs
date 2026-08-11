using SharedWorlds.Desktop;
using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class LegacyRemoteEnvironmentOptInTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData(" True ")]
    public void LegacyRemoteEnvironmentRequiresExplicitTrueOptIn(string value)
    {
        Assert.True(StewardDesktopRemoteConfiguration.IsLegacyRemoteEnvironmentEnabled(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("enabled")]
    public void AmbientOrImplicitValuesDoNotEnableLegacyRemoteEnvironment(string? value)
    {
        Assert.False(StewardDesktopRemoteConfiguration.IsLegacyRemoteEnvironmentEnabled(value));
    }

    [Fact]
    public void DefaultLoaderCountsEnvironmentOnlyBehindMigrationOptIn()
    {
        var source = ReadRepositoryFile(
            "src/SharedWorlds.Desktop/StewardDesktopRemoteConfiguration.cs");

        Assert.Contains(
            "IsLegacyRemoteEnvironmentRequested() && IsAnyEnvironmentConfigurationPresent()",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "STEWARD_ENABLE_LEGACY_REMOTE_MIGRATION",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "stale ambient STEWARD_* variables cannot",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DirectEnvironmentLoaderAlsoRequiresExplicitMigrationOptIn()
    {
        var source = ReadRepositoryFile(
            "src/SharedWorlds.Desktop/StewardDesktopRemoteConfiguration.cs");
        var loader = RequiredIndex(source, "internal static bool TryLoadFromEnvironment(");
        var enableRead = RequiredIndex(
            source,
            "Environment.GetEnvironmentVariable(LegacyRemoteEnvironmentEnableVariable)",
            loader);
        var enableGuard = RequiredIndex(
            source,
            "if (!IsLegacyRemoteEnvironmentEnabled(enableText))",
            enableRead);
        var apiRead = RequiredIndex(
            source,
            "Environment.GetEnvironmentVariable(ApiBaseAddressVariable)",
            enableGuard);

        Assert.True(loader < enableRead);
        Assert.True(enableRead < enableGuard);
        Assert.True(enableGuard < apiRead);
    }

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Required source fragment was not found: {value}");
        return index;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var candidate = Path.Combine(workspace, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }
}
