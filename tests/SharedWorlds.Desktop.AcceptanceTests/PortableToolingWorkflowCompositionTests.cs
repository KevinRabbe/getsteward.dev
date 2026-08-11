using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PortableToolingWorkflowCompositionTests
{
    [Theory]
    [InlineData(".github/workflows/portable-world-probe-ci.yml")]
    [InlineData(".github/workflows/portable-world-two-pc-test-kit.yml")]
    public void PortableEngineeringWorkflowIsManualOnly(string relativePath)
    {
        var source = ReadRepositoryFile(relativePath);

        Assert.Contains("on:\n  workflow_dispatch:", source, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request:", source, StringComparison.Ordinal);
        Assert.DoesNotContain("push:", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PortableAcceptanceToolingRemainsAvailableForExplicitEngineeringRuns()
    {
        var root = FindRepositoryRoot();
        var retainedPaths = new[]
        {
            "tools/SharedWorlds.PortableWorldProbe/SharedWorlds.PortableWorldProbe.csproj",
            "tools/SharedWorlds.PortableWorldProbe/Program.cs",
            "tools/build-portable-world-two-pc-kit.ps1",
            "docs/portable-world-two-pc-acceptance.md"
        };

        foreach (var relativePath in retainedPaths)
        {
            Assert.True(
                File.Exists(Path.Combine(root, relativePath)),
                $"Portable World engineering tool '{relativePath}' unexpectedly disappeared.");
        }
    }

    [Fact]
    public void PeerExactHeadGateDoesNotRequireManualPortableEngineeringArtifacts()
    {
        var source = ReadRepositoryFile(".github/workflows/exact-head-qualification-dynamic.yml");

        Assert.DoesNotContain("Portable World acceptance probe CI", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Portable World two-PC test kit", source, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath));

    private static string FindRepositoryRoot()
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace) &&
            File.Exists(Path.Combine(workspace, "SharedWorlds.sln")))
        {
            return workspace;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharedWorlds.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Steward repository root.");
    }
}
