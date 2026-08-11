using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class RetiredLegacyWorkflowCompositionTests
{
    private static readonly string[] RetiredWorkflowPaths =
    [
        ".github/workflows/closed-alpha-deployment-plan-ci.yml",
        ".github/workflows/closed-alpha-release-candidate.yml",
        ".github/workflows/closed-alpha-live-deployment-request.yml",
        ".github/workflows/closed-alpha-live-host-preflight.yml",
        ".github/workflows/closed-alpha-live-plan-staging.yml",
        ".github/workflows/closed-alpha-live-backend-deployment.yml",
        ".github/workflows/closed-alpha-live-ingress-activation.yml",
        ".github/workflows/closed-alpha-external-ingress-acceptance.yml",
        ".github/workflows/closed-alpha-live-host-execution.yml",
        ".github/workflows/closed-alpha-publication-authorization.yml",
        ".github/workflows/closed-beta-product.yml",
        ".github/workflows/bring-here-disposable-e2e.yml",
        ".github/workflows/bring-here-two-pc-test-kit.yml"
    ];

    [Fact]
    public void LegacyDeploymentAndBringHereWorkflowsAreNotRegisteredForNormalProductCi()
    {
        var root = FindRepositoryRoot();

        foreach (var relativePath in RetiredWorkflowPaths)
        {
            Assert.False(
                File.Exists(Path.Combine(root, relativePath)),
                $"Legacy workflow '{relativePath}' must not be registered in the normal peer product Actions directory.");
        }
    }

    [Fact]
    public void RetirementDoesNotDeleteMigrationAndHistoricalOperatorTooling()
    {
        var root = FindRepositoryRoot();
        var retainedTools = new[]
        {
            "tools/prepare-closed-alpha-live-deployment.ps1",
            "tools/build-closed-beta-product.ps1",
            "tools/build-bring-here-two-pc-kit.ps1"
        };

        foreach (var relativePath in retainedTools)
        {
            Assert.True(
                File.Exists(Path.Combine(root, relativePath)),
                $"Retained compatibility tool '{relativePath}' unexpectedly disappeared with workflow retirement.");
        }
    }

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
