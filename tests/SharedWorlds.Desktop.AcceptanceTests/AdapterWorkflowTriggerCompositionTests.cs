using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class AdapterWorkflowTriggerCompositionTests
{
    [Fact]
    public void SevenDaysToDieWorkflowRegistersOnlyForAdapterSpecificOrGlobalBuildChanges()
        => AssertAdapterWorkflow(
            ".github/workflows/seven-days-to-die-ci.yml",
            [
                "src/SharedWorlds.GameAdapters/SevenDaysToDie/**",
                "tests/SharedWorlds.GameAdapters.SevenDaysToDie.Tests/**",
                "tools/SharedWorlds.SevenDaysToDieProbe/**",
                ".github/workflows/seven-days-to-die-ci.yml"
            ]);

    [Fact]
    public void ProjectZomboidWorkflowRegistersOnlyForAdapterSpecificOrGlobalBuildChanges()
        => AssertAdapterWorkflow(
            ".github/workflows/project-zomboid-ci.yml",
            [
                "src/SharedWorlds.GameAdapters/ProjectZomboid/**",
                "tests/SharedWorlds.GameAdapters.ProjectZomboid.Tests/**",
                ".github/workflows/project-zomboid-ci.yml"
            ]);

    [Fact]
    public void PalworldWorkflowRegistersOnlyForAdapterSpecificOrGlobalBuildChanges()
        => AssertAdapterWorkflow(
            ".github/workflows/palworld-plm-probe-ci.yml",
            [
                "src/SharedWorlds.GameAdapters/Palworld/**",
                "tests/SharedWorlds.GameAdapters.Palworld.Tests/**",
                "tools/SharedWorlds.PalworldProbe/**",
                ".github/workflows/palworld-plm-probe-ci.yml"
            ]);

    [Fact]
    public void ExactHeadGatePaginatesChangedFilesAndMirrorsAdapterTriggerPaths()
    {
        var source = ReadRepositoryFile(".github/workflows/exact-head-qualification-dynamic.yml");

        Assert.Contains("pull-requests: read", source, StringComparison.Ordinal);
        Assert.Contains("pulls/$prNumber/files?per_page=100&page=$page", source, StringComparison.Ordinal);
        Assert.Contains("while ($pageFiles.Count -eq 100)", source, StringComparison.Ordinal);
        Assert.Contains("function Test-PathRelevant", source, StringComparison.Ordinal);

        var requiredFragments = new[]
        {
            "src/SharedWorlds.GameAdapters/SevenDaysToDie/",
            "tests/SharedWorlds.GameAdapters.SevenDaysToDie.Tests/",
            "tools/SharedWorlds.SevenDaysToDieProbe/",
            ".github/workflows/seven-days-to-die-ci.yml",
            "src/SharedWorlds.GameAdapters/ProjectZomboid/",
            "tests/SharedWorlds.GameAdapters.ProjectZomboid.Tests/",
            ".github/workflows/project-zomboid-ci.yml",
            "src/SharedWorlds.GameAdapters/Palworld/",
            "tests/SharedWorlds.GameAdapters.Palworld.Tests/",
            "tools/SharedWorlds.PalworldProbe/",
            ".github/workflows/palworld-plm-probe-ci.yml",
            "Directory.Build.props",
            "Directory.Packages.props",
            "global.json",
            "NuGet.config"
        };
        foreach (var fragment in requiredFragments)
        {
            Assert.Contains(fragment, source, StringComparison.Ordinal);
        }

        var commonStart = RequiredIndex(
            source,
            "$expectedNames = [Collections.Generic.List[string]]::new()");
        var commonProduct = RequiredIndex(
            source,
            "[void]$expectedNames.Add('Peer product CI')",
            commonStart);
        var commonPackage = RequiredIndex(
            source,
            "[void]$expectedNames.Add('Windows acceptance package')",
            commonProduct);
        var conditionalLoop = RequiredIndex(source, "foreach ($rule in $adapterRules)", commonPackage);
        var conditionalAdd = RequiredIndex(
            source,
            "[void]$expectedNames.Add([string]$rule.Name)",
            conditionalLoop);

        Assert.True(commonStart < commonProduct);
        Assert.True(commonProduct < commonPackage);
        Assert.True(commonPackage < conditionalLoop);
        Assert.True(conditionalLoop < conditionalAdd);
    }

    private static void AssertAdapterWorkflow(
        string relativePath,
        IReadOnlyList<string> adapterPaths)
    {
        var source = ReadRepositoryFile(relativePath);

        Assert.Contains("push:\n    branches:\n      - main\n    paths:", source, StringComparison.Ordinal);
        Assert.Contains("pull_request:\n    paths:", source, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", source, StringComparison.Ordinal);

        foreach (var path in adapterPaths)
        {
            Assert.Equal(2, CountOccurrences(source, $"- \"{path}\""));
        }

        foreach (var globalPath in new[]
                 {
                     "Directory.Build.props",
                     "Directory.Packages.props",
                     "global.json",
                     "NuGet.config"
                 })
        {
            Assert.Equal(2, CountOccurrences(source, $"- \"{globalPath}\""));
        }

        Assert.DoesNotContain("src/SharedWorlds.Core/**", source, StringComparison.Ordinal);
        Assert.DoesNotContain("src/SharedWorlds.Infrastructure/**", source, StringComparison.Ordinal);
        Assert.DoesNotContain("src/SharedWorlds.Desktop/**", source, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request:\n  workflow_dispatch:", source, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while (true)
        {
            var index = source.IndexOf(value, offset, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            offset = index + value.Length;
        }
    }

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Required source fragment was not found: {value}");
        return index;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var candidate = Path.Combine(workspace, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }
}
