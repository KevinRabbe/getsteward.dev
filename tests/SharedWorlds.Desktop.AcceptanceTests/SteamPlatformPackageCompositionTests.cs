using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamPlatformPackageCompositionTests
{
    [Fact]
    public void NormalSteamProductIsRequestedByAppIdAlone()
    {
        var source = ReadBuildScript();

        Assert.Contains("[uint32]$SteamReleaseAppId = 0,", source, StringComparison.Ordinal);
        Assert.Contains("$steamReleaseRequested = $SteamReleaseAppId -ne 0", source, StringComparison.Ordinal);
        Assert.Contains("Deployment: Steam peer product package", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SteamReleaseApiBaseUrl is required when Steam release package configuration is requested",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NormalSteamProductWritesOnlyIndependentPlatformIdentityByDefault()
    {
        var source = ReadBuildScript();
        var steamBlock = RequiredIndex(source, "if ($steamReleaseRequested) {");
        var platform = RequiredIndex(source, "$steamPlatformConfiguration = [ordered]@{", steamBlock);
        var platformFile = RequiredIndex(
            source,
            "$steamPlatformConfigurationPath = Join-Path $output 'steward-steam.json'",
            platform);
        var legacyBlock = RequiredIndex(source, "if ($legacyRemoteMigrationRequested) {", platformFile);
        var legacyFile = RequiredIndex(
            source,
            "$steamReleaseConfigurationPath = Join-Path $output 'steward-steam-release.json'",
            legacyBlock);

        Assert.True(steamBlock < platform);
        Assert.True(platform < platformFile);
        Assert.True(platformFile < legacyBlock);
        Assert.True(legacyBlock < legacyFile);
        Assert.Contains("schemaVersion = 1\n        steamAppId = $SteamReleaseAppId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyRemoteConfigurationRequiresExplicitMigrationSwitch()
    {
        var source = ReadBuildScript();

        Assert.Contains("[switch]$IncludeLegacyRemoteMigrationConfiguration,", source, StringComparison.Ordinal);
        Assert.Contains(
            "$legacyRemoteMigrationRequested = $IncludeLegacyRemoteMigrationConfiguration.IsPresent",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if ($legacyRemoteMigrationRequested) {",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "SteamReleaseApiBaseUrl is required only when legacy Steam remote migration configuration is requested",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Legacy API coordinates are additionally embedded in steward-steam-release.json because migration compatibility was explicitly requested.",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OldRemoteArgumentsWithoutMigrationSwitchAreIgnoredInsteadOfEmbedded()
    {
        var source = ReadBuildScript();
        var supplied = RequiredIndex(source, "$legacyRemoteCoordinatesSupplied =");
        var migration = RequiredIndex(source, "if ($legacyRemoteMigrationRequested) {", supplied);
        var ignored = RequiredIndex(source, "elseif ($legacyRemoteCoordinatesSupplied) {", migration);
        var warning = RequiredIndex(
            source,
            "Legacy Steam API coordinates were supplied but will not be embedded.",
            ignored);

        Assert.True(supplied < migration);
        Assert.True(migration < ignored);
        Assert.True(ignored < warning);
    }

    [Fact]
    public void PlatformConfigurationCarriesNoBackendCoordinatesOrSecrets()
    {
        var source = ReadBuildScript();
        var platformStart = RequiredIndex(source, "$steamPlatformConfiguration = [ordered]@{");
        var platformEnd = RequiredIndex(source, "if ($legacyRemoteMigrationRequested) {", platformStart);
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

    [Fact]
    public void PlatformConfigurationWriteIsManifestCovered()
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
    public void NormalProductMessageExplicitlyRejectsRemoteSessionConfiguration()
    {
        var source = ReadBuildScript();
        var normalProduct = RequiredIndex(
            source,
            "elseif ($steamReleaseRequested) {");
        var statement = RequiredIndex(
            source,
            "No legacy API URL, Web API identity, backend credential, or remote-session configuration is embedded in the normal product package.",
            normalProduct);

        Assert.True(normalProduct < statement);
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
