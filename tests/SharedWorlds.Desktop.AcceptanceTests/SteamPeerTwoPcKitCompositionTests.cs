using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamPeerTwoPcKitCompositionTests
{
    [Fact]
    public void PhysicalKitWorkflowIsManualOnlyAndRequiresOnlyNonSecretSteamBuildIdentity()
    {
        var workflow = Read(".github/workflows/steam-peer-two-pc-test-kit.yml");

        Assert.Contains("workflow_dispatch:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("push:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("schedule:", workflow, StringComparison.Ordinal);
        Assert.Contains("steam_app_id:", workflow, StringComparison.Ordinal);
        Assert.Contains("windows_depot_id:", workflow, StringComparison.Ordinal);
        Assert.Contains("version:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("api_base", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("web_api", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("friends_build", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password:", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("steam_guard", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("build-steam-peer-two-pc-kit.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/upload-artifact@v6", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalKitWorkflowGeneratesSteamPipeInputsAsSeparateArtifactWithoutMutatingKit()
    {
        var workflow = Read(".github/workflows/steam-peer-two-pc-test-kit.yml");
        var kitBuild = RequiredIndex(workflow, "-OutputDirectory $output");
        var generator = RequiredIndex(workflow, "./tools/new-steam-peer-beta-steampipe.ps1", kitBuild);
        var product = RequiredIndex(workflow, "-ProductDirectory (Join-Path $kit 'product')", generator);
        var steamPipeOutput = RequiredIndex(workflow, "-OutputDirectory $steamPipe", product);
        var kitUpload = RequiredIndex(workflow, "name: steward-steam-peer-two-pc-${{ inputs.version }}-${{ github.sha }}", steamPipeOutput);
        var steamPipeUpload = RequiredIndex(workflow, "name: steward-steampipe-rc-input-${{ inputs.version }}-${{ github.sha }}", kitUpload);

        Assert.True(kitBuild < generator);
        Assert.True(generator < product);
        Assert.True(product < steamPipeOutput);
        Assert.True(steamPipeOutput < kitUpload);
        Assert.True(kitUpload < steamPipeUpload);
        Assert.Contains("$env:RUNNER_TEMP/steward-steam-peer-two-pc-kit", workflow, StringComparison.Ordinal);
        Assert.Contains("$env:RUNNER_TEMP/steward-steampipe-rc-input", workflow, StringComparison.Ordinal);
        Assert.Contains("windows_depot_id must be a positive UInt32", workflow, StringComparison.Ordinal);
        Assert.Contains("steampipe-rc-input.json", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("SetLive", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("steamcmd", workflow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuilderKeepsNormalProductSeparateAndNeverEnablesLegacyMigrationPackaging()
    {
        var builder = Read("tools/build-steam-peer-two-pc-kit.ps1");
        var build = RequiredIndex(builder, "$buildArguments = @{");
        var appId = RequiredIndex(builder, "SteamReleaseAppId = $SteamAppId", build);
        var product = RequiredIndex(builder, "$productOutput = Join-Path $output 'product'");
        var tools = RequiredIndex(builder, "$toolOutput = Join-Path $output 'acceptance-tools'", product);

        Assert.True(product < tools);
        Assert.Contains("ReleaseContentOnly = $true", builder[build..], StringComparison.Ordinal);
        Assert.True(build < appId);
        Assert.DoesNotContain("IncludeLegacyRemoteMigrationConfiguration", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamReleaseApiBaseUrl", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamReleaseWebApiIdentity", builder, StringComparison.Ordinal);
        Assert.DoesNotContain("FriendsBuildApiBaseUrl", builder, StringComparison.Ordinal);
        Assert.Contains("steward-steam-release.json", builder, StringComparison.Ordinal);
        Assert.Contains("steward-friends-build.json", builder, StringComparison.Ordinal);
        Assert.Contains("unexpectedly contains migration file", builder, StringComparison.Ordinal);
        Assert.Contains("--verify-package", builder, StringComparison.Ordinal);
        Assert.Contains("peer-test-kit.json", builder, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceProbeIsReadOnlyLocalAndRejectsMigrationConfiguredProduct()
    {
        var probe = Read("tools/SharedWorlds.PeerWorldProbe/Program.cs");

        Assert.Contains("new LocalWorldStorage(dataRoot)", probe, StringComparison.Ordinal);
        Assert.Contains("storage.LoadWorldAsync(worldId)", probe, StringComparison.Ordinal);
        Assert.Contains("storage.LoadStateRevisionAsync(worldId, stateId)", probe, StringComparison.Ordinal);
        Assert.Contains("storage.LoadEnvironmentRevisionAsync(worldId, environmentId)", probe, StringComparison.Ordinal);
        Assert.Contains("storage.OpenRevisionAsync(worldId, stateId)", probe, StringComparison.Ordinal);
        Assert.Contains("IncrementalHash.CreateHash(HashAlgorithmName.SHA256)", probe, StringComparison.Ordinal);
        Assert.Contains("steward-steam-release.json", probe, StringComparison.Ordinal);
        Assert.Contains("steward-friends-build.json", probe, StringComparison.Ordinal);
        Assert.Contains("cannot contain legacy remote migration configuration", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("Steamworks", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("SharedWorlds.Infrastructure.Remote", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveWorldAsync", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("StoreRevisionAsync", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteWorldAsync", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("EvictRevisionPayloadAsync", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeBindsPackageInstallationAuthorityAndExactCurrentPayloadIntoEvidence()
    {
        var probe = Read("tools/SharedWorlds.PeerWorldProbe/Program.cs");

        Assert.Contains("PackageEvidence Package", probe, StringComparison.Ordinal);
        Assert.Contains("string InstallationId", probe, StringComparison.Ordinal);
        Assert.Contains("AuthorityEvidence Authority", probe, StringComparison.Ordinal);
        Assert.Contains("string CurrentStateRevisionId", probe, StringComparison.Ordinal);
        Assert.Contains("string CurrentEnvironmentRevisionId", probe, StringComparison.Ordinal);
        Assert.Contains("long PayloadByteSize", probe, StringComparison.Ordinal);
        Assert.Contains("string PayloadSha256", probe, StringComparison.Ordinal);
        Assert.Contains("environment.Manifest.AdapterId", probe, StringComparison.Ordinal);
        Assert.Contains("state.EnvironmentRevisionId != environmentId", probe, StringComparison.Ordinal);
        Assert.Contains("--list", probe, StringComparison.Ordinal);
        Assert.Contains("--verify-package", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalProcedureCoversCorePeerLifecycleAndAccessSemantics()
    {
        var guide = Read("tools/steam-peer-two-pc-kit/START-HERE-STEAM-PEER-TWO-PC.txt");

        Assert.Contains("PHASE 1 — create, share, add friend", guide, StringComparison.Ordinal);
        Assert.Contains("PHASE 2 — Host, Steam invite, Join/bootstrap", guide, StringComparison.Ordinal);
        Assert.Contains("PHASE 3 — exact live host handoff A -> B", guide, StringComparison.Ordinal);
        Assert.Contains("authority.generation = 1", guide, StringComparison.Ordinal);
        Assert.Contains("authority generation = 2", guide, StringComparison.Ordinal);
        Assert.Contains("ordinary stop/restart MUST NOT create a new generation", guide, StringComparison.Ordinal);
        Assert.Contains("PHASE 5 — live Remove access, then safe re-add", guide, StringComparison.Ordinal);
        Assert.Contains("PHASE 6 — true Leave World", guide, StringComparison.Ordinal);
        Assert.Contains("No central Steward backend is part of the normal runtime.", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureHelpersUseExternalEvidenceAndProductSubdirectory()
    {
        var list = Read("tools/steam-peer-two-pc-kit/LIST-PEER-WORLDS.cmd");
        var capture = Read("tools/steam-peer-two-pc-kit/CAPTURE-PEER-EVIDENCE.cmd");

        Assert.Contains("acceptance-tools\\SharedWorlds.PeerWorldProbe.exe\" --list", list, StringComparison.Ordinal);
        Assert.Contains("--package-root \"%~dp0product\"", capture, StringComparison.Ordinal);
        Assert.Contains("%USERPROFILE%\\Desktop\\steward-peer-evidence", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("steward-steam-release.json", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("http", capture, StringComparison.OrdinalIgnoreCase);
    }

    private static string Read(string relativePath)
        => File.ReadAllText(FindRepositoryFile(relativePath));

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
