using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamPlatformPackageCompositionTests
{
    [Fact]
    public void SteamReleaseWritesIndependentPlatformConfigurationFromExistingAppIdInput()
    {
        var source = ReadBuildScript();
        var releaseStart = RequiredIndex(source, "if ($steamReleaseRequested) {");
        var platformConfiguration = RequiredIndex(
            source,
            "$steamPlatformConfiguration = [ordered]@{",
            releaseStart);
        var platformFile = RequiredIndex(
            source,
            "$steamPlatformConfigurationPath = Join-Path $output 'steward-steam.json'",
            platformConfiguration);
        var legacyConfiguration = RequiredIndex(
            source,
            "$steamReleaseConfiguration = [ordered]@{",
            platformFile);

        Assert.Contains("schemaVersion = 1\n        steamAppId = $SteamReleaseAppId", source, StringComparison.Ordinal);
        Assert.True(releaseStart < platformConfiguration);
        Assert.True(platformConfiguration < platformFile);
        Assert.True(platformFile < legacyConfiguration);
        Assert.Contains("steamAppId = $SteamReleaseAppId", source[legacyConfiguration..], StringComparison.Ordinal);
    }

    [Fact]
    public void SteamReleaseUsesOneValidatedAppIdParameterForBothMigrationFiles()
    {
        var source = ReadBuildScript();

        Assert.Contains("[uint32]$SteamReleaseAppId = 0,", source, StringComparison.Ordinal);
        Assert.Contains("if ($SteamReleaseAppId -eq 0)", source, StringComparison.Ordinal);
        Assert.Contains("Fail 'SteamReleaseAppId must be a positive UInt32", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamPlatformAppId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PeerSteamAppId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EmbeddedSteamAppId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SteamPlatformFileIsCreatedBeforeManifestEnumeratesReleaseContent()
    {
        var source = ReadBuildScript();
        var platformWrite = RequiredIndex(
            source,
            "$steamPlatformConfigurationPath = Join-Path $output 'steward-steam.json'");
        var packageEnumeration = RequiredIndex(
            source,
            "$packageFiles = @(Get-ChildItem -LiteralPath $output -Recurse -File",
            platformWrite);

        Assert.True(platformWrite < packageEnumeration);
        Assert.Contains(
            "[IO.File]::WriteAllText(\n        $steamPlatformConfigurationPath,",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NonSteamPackagePathDoesNotCreatePlatformConfiguration()
    {
        var source = ReadBuildScript();
        var steamBlock = RequiredIndex(source, "if ($steamReleaseRequested) {");
        var platformWrite = RequiredIndex(
            source,
            "$steamPlatformConfigurationPath = Join-Path $output 'steward-steam.json'",
            steamBlock);
        var nextTopLevelSection = RequiredIndex(
            source,
            "$desktopExecutable = Join-Path $output 'SharedWorlds.Desktop.exe'",
            platformWrite);

        Assert.True(steamBlock < platformWrite);
        Assert.True(platformWrite < nextTopLevelSection);
        Assert.DoesNotContain(
            "steward-steam.json",
            source[..steamBlock],
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "steward-steam.json",
            source[nextTopLevelSection..],
            StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentPlatformFileCarriesNoBackendCoordinatesOrSecrets()
    {
        var source = ReadBuildScript();
        var platformStart = RequiredIndex(source, "$steamPlatformConfiguration = [ordered]@{");
        var platformEnd = RequiredIndex(source, "$steamReleaseConfiguration = [ordered]@{", platformStart);
        var platformSection = source[platformStart..platformEnd];

        Assert.Contains("schemaVersion = 1", platformSection, StringComparison.Ordinal);
        Assert.Contains("steamAppId = $SteamReleaseAppId", platformSection, StringComparison.Ordinal);
        Assert.DoesNotContain("apiBaseUrl", platformSection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WebApi", platformSection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("identity", platformSection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", platformSection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", platformSection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", platformSection, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadBuildScript()
        => File.ReadAllText(FindRepositoryFile("tools/e4-build-desktop.ps1"));

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Required source fragment was not found: {value}");
        return index;
    }

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
