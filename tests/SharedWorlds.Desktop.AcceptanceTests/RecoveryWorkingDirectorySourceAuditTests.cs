using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class RecoveryWorkingDirectorySourceAuditTests
{
    private static readonly string[] RecoveryDecisionAndUiSources =
    [
        "src/SharedWorlds.Core/Worlds/InterruptedWorkspaceRecoveryDecisionService.cs",
        "src/SharedWorlds.Core/Worlds/WorkspaceCleanupRecoveryService.cs",
        "src/SharedWorlds.Desktop/MainWindow.CleanupRecovery.cs",
        "src/SharedWorlds.Desktop/MainWindow.RecoveryExport.cs"
    ];

    private static readonly string[] ForbiddenRecoveryPathAccesses =
    [
        "record.WorkingDirectory",
        "recovery.WorkingDirectory",
        "workspaceRecord.WorkingDirectory",
        "unresolved.WorkingDirectory"
    ];

    [Fact]
    public void RecoveryDecisionAndUiLayersDoNotTrustJournaledWorkingDirectory()
    {
        var repositoryRoot = FindRepositoryRoot();
        var violations = new List<string>();

        foreach (var relativePath in RecoveryDecisionAndUiSources)
        {
            var path = Path.Combine(
                repositoryRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            var source = File.ReadAllText(path);

            foreach (var forbidden in ForbiddenRecoveryPathAccesses)
            {
                if (source.Contains(forbidden, StringComparison.Ordinal))
                {
                    violations.Add($"{relativePath}: directly reads {forbidden}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Persisted recovery path authority leaked outside PreparedWorldRecoveryResolver:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void ResolverIsTheExplicitLegacyWorkingDirectoryCompatibilityBoundary()
    {
        var repositoryRoot = FindRepositoryRoot();
        var resolverPath = Path.Combine(
            repositoryRoot,
            "src",
            "SharedWorlds.Core",
            "Worlds",
            "PreparedWorldRecoveryResolver.cs");
        var source = File.ReadAllText(resolverPath);

        Assert.Contains("recovery.WorkingDirectory", source, StringComparison.Ordinal);
        Assert.Contains("Compatibility only", source, StringComparison.Ordinal);
        Assert.Contains("RecoveryLocation", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace) &&
            Directory.Exists(Path.Combine(workspace, "src")))
        {
            return workspace;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate repository root for source audit.");
    }
}
