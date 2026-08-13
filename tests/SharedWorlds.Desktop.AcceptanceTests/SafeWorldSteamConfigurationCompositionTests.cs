using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SafeWorldSteamConfigurationCompositionTests
{
    [Fact]
    public void PublicSteamConfigurationUsesSafeWorldNamesAndKeepsLegacyNamesLocal()
    {
        var source = ReadRepositoryFile(
            "src/SharedWorlds.Desktop/SafeWorldDesktopSteamConfiguration.cs");

        Assert.Contains(
            "PackageConfigurationFileName = \"safeworld-steam.json\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SteamAppIdVariable = \"SAFEWORLD_STEAM_APP_ID\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const string LegacyPackageConfigurationFileName = \"steward-steam.json\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const string LegacySteamAppIdVariable = \"STEWARD_STEAM_APP_ID\"",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "StewardDesktopSteamConfiguration.PackageConfigurationFileName",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "StewardDesktopSteamConfiguration.SteamAppIdVariable",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SafeWorldConfigurationFailsClosedWhenCanonicalAndLegacySourcesAreMixed()
    {
        var source = ReadRepositoryFile(
            "src/SharedWorlds.Desktop/SafeWorldDesktopSteamConfiguration.cs");

        Assert.Contains("configuredSourceCount > 1", source, StringComparison.Ordinal);
        Assert.Contains(
            "Configure exactly one SafeWorld or legacy AppID source",
            source,
            StringComparison.Ordinal);
        Assert.Contains("File.Exists(canonicalPath)", source, StringComparison.Ordinal);
        Assert.Contains("File.Exists(legacyPath)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupCompatibilityBridgeFeedsOnlyTheQualifiedInternalAppIdInput()
    {
        var source = ReadRepositoryFile(
            "src/SharedWorlds.Desktop/SafeWorldSteamCompatibilityBootstrap.cs");

        Assert.Contains("[ModuleInitializer]", source, StringComparison.Ordinal);
        Assert.Contains(
            "SafeWorldDesktopSteamConfiguration.TryLoad",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const string LegacySteamAppIdVariable = \"STEWARD_STEAM_APP_ID\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains("InvalidConfiguration", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "StewardDesktopSteamConfiguration.SteamAppIdVariable",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PublicBetaPackagerEmitsOnlyCanonicalSafeWorldSteamFilename()
    {
        var source = ReadRepositoryFile("tools/build-safeworld-public-beta.ps1");

        Assert.Contains("'safeworld-steam.json'", source, StringComparison.Ordinal);
        Assert.Contains(
            "Public beta product must not contain the legacy Steam configuration filename",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "steamConfiguration = 'safeworld-steam.json'",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AppID 480 is development-only",
            source,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
        => File.ReadAllText(FindRepositoryFile(relativePath));

    private static string FindRepositoryFile(string relativePath)
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var candidate = Path.Combine(workspace, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }
}
