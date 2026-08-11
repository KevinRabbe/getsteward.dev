using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PeerProductCiCompositionTests
{
    [Fact]
    public void PeerSolutionBoundaryContainsProductProjectsButNoLegacyBackend()
    {
        var source = ReadRepositoryFile("SharedWorlds.PeerProduct.slnf");

        Assert.Contains("SharedWorlds.Core", source, StringComparison.Ordinal);
        Assert.Contains("SharedWorlds.Infrastructure", source, StringComparison.Ordinal);
        Assert.Contains("SharedWorlds.Desktop", source, StringComparison.Ordinal);
        Assert.Contains("SharedWorlds.Core.Tests", source, StringComparison.Ordinal);
        Assert.Contains("SharedWorlds.Infrastructure.Tests", source, StringComparison.Ordinal);
        Assert.Contains("SharedWorlds.Desktop.AcceptanceTests", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SharedWorlds.Backend", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultCiUsesPeerBoundaryAndDoesNotProvisionBackendInfrastructure()
    {
        var source = ReadRepositoryFile(".github/workflows/ci.yml");

        Assert.Contains("name: Peer product CI", source, StringComparison.Ordinal);
        Assert.Contains("SharedWorlds.PeerProduct.slnf", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SharedWorlds.Backend", source, StringComparison.Ordinal);
        Assert.DoesNotContain("postgres:", source, StringComparison.Ordinal);
        Assert.DoesNotContain("minio", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionStrings__Steward", source, StringComparison.Ordinal);
        Assert.DoesNotContain("STEWARD_TEST_POSTGRES", source, StringComparison.Ordinal);
        Assert.DoesNotContain("STEWARD_TEST_S3", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PeerExactHeadGateRequiresOnlyNormalProductWorkflows()
    {
        var source = ReadRepositoryFile(".github/workflows/exact-head-qualification-dynamic.yml");

        Assert.Contains("'Peer product CI'", source, StringComparison.Ordinal);
        Assert.Contains("'Windows acceptance package'", source, StringComparison.Ordinal);
        Assert.Contains("'Palworld read-only CI'", source, StringComparison.Ordinal);
        Assert.Contains("'7 Days to Die adapter CI'", source, StringComparison.Ordinal);
        Assert.Contains("'Project Zomboid adapter CI'", source, StringComparison.Ordinal);
        Assert.Contains("qualificationBoundary = 'normal-peer-product-only'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("closed-alpha", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("closed-beta", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bring Here", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Legacy backend CI", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyBackendQualificationIsSeparateAndPathScoped()
    {
        var source = ReadRepositoryFile(".github/workflows/legacy-backend-ci.yml");

        Assert.Contains("name: Legacy backend CI", source, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", source, StringComparison.Ordinal);
        Assert.Contains("pull_request:", source, StringComparison.Ordinal);
        Assert.Contains("paths:", source, StringComparison.Ordinal);
        Assert.Contains("src/SharedWorlds.Backend.Api/**", source, StringComparison.Ordinal);
        Assert.Contains("Legacy PostgreSQL integration", source, StringComparison.Ordinal);
        Assert.Contains("Legacy S3-compatible integration", source, StringComparison.Ordinal);
        Assert.Contains("Legacy backend container", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SupersededLegacyExactHeadAggregateIsRemoved()
    {
        Assert.False(File.Exists(FindRepositoryFile(".github/workflows/exact-head-qualification.yml")));
    }

    private static string ReadRepositoryFile(string relativePath)
        => File.ReadAllText(FindRepositoryFile(relativePath));

    private static string FindRepositoryFile(string relativePath)
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            return Path.Combine(workspace, relativePath);
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate) || Directory.Exists(Path.GetDirectoryName(candidate)))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate repository path '{relativePath}'.");
    }
}
