using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class DesktopLocalDataRootSourceAuditTests
{
    [Fact]
    public void DesktopSourcesHaveNoIndependentLegacyLocalDataRoots()
    {
        var desktopRoot = FindRepositoryDirectory("src/SharedWorlds.Desktop");
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(
                     desktopRoot,
                     "*.cs",
                     SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(
                    Path.GetFileName(path),
                    "DesktopLocalDataRoot.cs",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(path);
            var relativePath = Path.GetRelativePath(desktopRoot, path);
            if (source.Contains("\"SharedWorlds\"", StringComparison.Ordinal))
            {
                violations.Add($"{relativePath}: legacy SharedWorlds root literal");
            }

            if (source.Contains(
                    "Environment.SpecialFolder.LocalApplicationData",
                    StringComparison.Ordinal))
            {
                violations.Add($"{relativePath}: independent LocalApplicationData resolution");
            }

            if (source.Contains("GetLocalDataRoot", StringComparison.Ordinal))
            {
                violations.Add($"{relativePath}: removed GetLocalDataRoot helper");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Desktop local-data root violations:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    private static string FindRepositoryDirectory(string relativePath)
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var candidate = Path.Combine(workspace, relativePath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repository directory '{relativePath}'.");
    }
}
