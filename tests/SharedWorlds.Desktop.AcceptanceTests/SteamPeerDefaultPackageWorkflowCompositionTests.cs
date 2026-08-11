using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamPeerDefaultPackageWorkflowCompositionTests
{
    [Fact]
    public void WorkflowExposesLegacyRemoteConfigurationOnlyAsExplicitOptIn()
    {
        var source = ReadWorkflow();

        Assert.Contains("steam_include_legacy_remote_migration:", source, StringComparison.Ordinal);
        Assert.Contains("default: false", source, StringComparison.Ordinal);
        Assert.Contains(
            "REQUESTED_LEGACY_REMOTE_MIGRATION: ${{ github.event.inputs.steam_include_legacy_remote_migration }}",
            source,
            StringComparison.Ordinal);
        Assert.Contains("$includeLegacyRemoteMigration = [string]::Equals(", source, StringComparison.Ordinal);
        Assert.Contains("if ($includeLegacyRemoteMigration) {", source, StringComparison.Ordinal);
        Assert.Contains(
            "$buildArguments.IncludeLegacyRemoteMigrationConfiguration = $true",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NormalSteamDepotUsesNamedAppIdParametersButNoBackendCoordinates()
    {
        var source = ReadWorkflow();
        var step = RequiredIndex(source, "- name: Build and verify V3 Steam depot content");
        var defaultAppId = RequiredIndex(source, "[uint32]$appId = 123456789", step);
        var buildArgs = RequiredIndex(source, "$buildArguments = @{", defaultAppId);
        var appId = RequiredIndex(source, "SteamReleaseAppId = $appId", buildArgs);
        var releaseOnly = RequiredIndex(source, "ReleaseContentOnly = $true", buildArgs);
        var invocation = RequiredIndex(source, "& ./tools/e4-build-desktop.ps1 @buildArguments", releaseOnly);
        var migration = RequiredIndex(source, "if ($includeLegacyRemoteMigration) {", invocation);

        Assert.True(step < defaultAppId);
        Assert.True(defaultAppId < buildArgs);
        Assert.True(buildArgs < appId);
        Assert.True(buildArgs < releaseOnly);
        Assert.True(releaseOnly < invocation);
        Assert.True(invocation < migration);
        var normalBuildSection = source[buildArgs..invocation];
        Assert.DoesNotContain("SteamReleaseApiBaseUrl", normalBuildSection, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamReleaseWebApiIdentity", normalBuildSection, StringComparison.Ordinal);
        Assert.DoesNotContain("'-SteamReleaseAppId'", normalBuildSection, StringComparison.Ordinal);
        Assert.DoesNotContain("$buildArguments = @(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalDepotFailsIfLegacyRemoteArtifactsAppear()
    {
        var source = ReadWorkflow();
        var legacyElse = RequiredIndex(source, "else {\n            if ([IO.File]::Exists($legacyConfigurationPath))");
        var remoteFile = RequiredIndex(
            source,
            "Normal Steam peer depot must not contain steward-steam-release.json.",
            legacyElse);
        var backendEvidence = RequiredIndex(
            source,
            "Normal Steam peer release evidence must not contain backend auth configuration.",
            remoteFile);

        Assert.True(legacyElse < remoteFile);
        Assert.True(remoteFile < backendEvidence);
    }

    [Fact]
    public void DownloadablePeerDepotArtifactDependsOnlyOnExplicitSteamAppId()
    {
        var source = ReadWorkflow();
        var upload = RequiredIndex(source, "- name: Upload downloadable Steam depot content");
        var condition = RequiredIndex(
            source,
            "github.event.inputs.steam_app_id != ''",
            upload);
        var nextUpload = RequiredIndex(source, "- name: Upload Steam depot release evidence", condition);
        var block = source[upload..nextUpload];

        Assert.True(upload < condition);
        Assert.DoesNotContain("steam_api_base_url", block, StringComparison.Ordinal);
        Assert.DoesNotContain("steam_web_api_identity", block, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformConfigurationIsAlwaysVerifiedAsAppIdentityOnly()
    {
        var source = ReadWorkflow();
        var platformPath = RequiredIndex(source, "$platformConfigurationPath = Join-Path $output 'steward-steam.json'");
        var expectedProperties = RequiredIndex(
            source,
            "$expectedPlatformProperties = @('schemaVersion', 'steamAppId')",
            platformPath);
        var legacyPath = RequiredIndex(
            source,
            "$legacyConfigurationPath = Join-Path $output 'steward-steam-release.json'",
            platformPath);

        Assert.True(platformPath < expectedProperties);
        Assert.True(platformPath < legacyPath);
    }

    private static string ReadWorkflow()
        => File.ReadAllText(FindRepositoryFile(
            ".github/workflows/windows-acceptance-package.yml"));

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
