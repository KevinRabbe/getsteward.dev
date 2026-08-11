using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class StewardDesktopSteamConfigurationCompositionTests
{
    [Fact]
    public void SteamPlatformConfigurationContainsOnlyExpectedAppIdentity()
    {
        var source = ReadConfiguration();

        Assert.Contains(
            "internal sealed record StewardDesktopSteamConfiguration(uint AppId)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal const string PackageConfigurationFileName = \"steward-steam.json\";",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal const string SteamAppIdVariable = \"STEWARD_STEAM_APP_ID\";",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ApiBaseAddress", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WebApi", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Infrastructure.Remote", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StewardRemoteSessionTokens", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamWebApiTicketSource", source, StringComparison.Ordinal);
        Assert.DoesNotContain("accessToken", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refreshToken", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("steamWebApiIdentity", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SteamPlatformConfigurationFailsClosedOnAmbiguousOrMalformedSources()
    {
        var source = ReadConfiguration();

        Assert.Contains("if (environmentConfigured && packageConfigured)", source, StringComparison.Ordinal);
        Assert.Contains("configure either {PackageConfigurationFileName} or {SteamAppIdVariable}, not both", source, StringComparison.Ordinal);
        Assert.Contains("FileAttributes.ReparsePoint", source, StringComparison.Ordinal);
        Assert.Contains("MaximumConfigurationBytes = 1024", source, StringComparison.Ordinal);
        Assert.Contains("AllowTrailingCommas = false", source, StringComparison.Ordinal);
        Assert.Contains("CommentHandling = JsonCommentHandling.Disallow", source, StringComparison.Ordinal);
        Assert.Contains("contains unsupported field", source, StringComparison.Ordinal);
        Assert.Contains("schemaVersion != SchemaVersion", source, StringComparison.Ordinal);
        Assert.Contains("appId is null or 0", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SteamPlatformConfigurationDoesNotRequireRemoteReleaseConfiguration()
    {
        var source = ReadConfiguration();

        Assert.DoesNotContain("steward-steam-release.json", source, StringComparison.Ordinal);
        Assert.DoesNotContain("steward-friends-build.json", source, StringComparison.Ordinal);
        Assert.DoesNotContain("STEWARD_API_BASE_URL", source, StringComparison.Ordinal);
        Assert.DoesNotContain("STEWARD_AUTH_MODE", source, StringComparison.Ordinal);
        Assert.DoesNotContain("STEWARD_STEAM_WEB_API_IDENTITY", source, StringComparison.Ordinal);
    }

    private static string ReadConfiguration()
        => File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/StewardDesktopSteamConfiguration.cs"));

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
