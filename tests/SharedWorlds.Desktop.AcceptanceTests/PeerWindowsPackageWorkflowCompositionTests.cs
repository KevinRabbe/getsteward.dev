using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PeerWindowsPackageWorkflowCompositionTests
{
    [Fact]
    public void FriendsBuildCompatibilityRunsOnlyForExplicitManualBackendCoordinate()
    {
        var source = ReadWorkflow();
        const string explicitLegacyCondition =
            "if: ${{ github.event_name == 'workflow_dispatch' && github.event.inputs.friends_api_base_url != '' }}";

        var build = RequiredIndex(source, "- name: Build and verify V2 Friends Build ZIP");
        var buildCondition = RequiredIndex(source, explicitLegacyCondition, build);
        var provision = RequiredIndex(source, "- name: Verify Friends Build friend provisioning", buildCondition);
        var provisionCondition = RequiredIndex(source, explicitLegacyCondition, provision);
        var upload = RequiredIndex(source, "- name: Upload downloadable Friends Build artifact", provisionCondition);
        var uploadCondition = RequiredIndex(source, explicitLegacyCondition, upload);

        Assert.True(build < buildCondition);
        Assert.True(buildCondition < provision);
        Assert.True(provision < provisionCondition);
        Assert.True(provisionCondition < upload);
        Assert.True(upload < uploadCondition);
    }

    [Fact]
    public void NormalPeerPackageNeverSynthesizesFriendsBackendCoordinates()
    {
        var source = ReadWorkflow();

        Assert.DoesNotContain("friends-build.example.invalid", source, StringComparison.Ordinal);
        Assert.Contains(
            "throw 'friends_api_base_url is required for explicit Friends Build qualification.'",
            source,
            StringComparison.Ordinal);
    }

    private static string ReadWorkflow()
        => File.ReadAllText(FindRepositoryFile(".github/workflows/windows-acceptance-package.yml"));

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
            if (File.Exists(candidate)) { return candidate; }
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) { return candidate; }
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }
}
