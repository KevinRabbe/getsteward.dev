using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class WorldHistoryPresentationTests
{
    [Fact]
    public void HistoryIsAContextualLocalWorldAction()
    {
        var startup = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.UnifiedStartup.cs"));
        var history = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.WorldHistory.cs"));
        var busy = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.xaml.cs"));

        Assert.Contains("InitializeWorldHistoryUi();", startup, StringComparison.Ordinal);
        Assert.Contains("Content = DesktopText.History", history, StringComparison.Ordinal);
        Assert.Contains("world.SharingMode == WorldSharingMode.LocalOnly", history, StringComparison.Ordinal);
        Assert.Contains("!_remoteWorldIds.Contains(world.Id)", history, StringComparison.Ordinal);
        Assert.Contains("WorldLifecycleResponsibilityKind.None", history, StringComparison.Ordinal);
        Assert.Contains("UpdateWorldHistoryActionState();", busy, StringComparison.Ordinal);
        Assert.DoesNotContain("_remoteRuntime", history, StringComparison.Ordinal);
    }

    [Fact]
    public void RestorePreservesBothHistoriesAndCopyCreatesIndependentWorld()
    {
        var history = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.WorldHistory.cs"));
        var dialog = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/WorldHistoryDialog.cs"));
        var service = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Core/Worlds/WorldHistoryService.cs"));

        Assert.Contains("Every later History entry remains available", history, StringComparison.Ordinal);
        Assert.Contains("service.RestoreAsync(", history, StringComparison.Ordinal);
        Assert.Contains("service.MakeIndependentCopyAsync(", history, StringComparison.Ordinal);
        Assert.Contains("CreateUniqueWorldNameAsync", history, StringComparison.Ordinal);
        Assert.Contains("separate local World", history, StringComparison.Ordinal);

        Assert.Contains("DesktopText.CurrentState", dialog, StringComparison.Ordinal);
        Assert.Contains("DesktopText.EarlierSave", dialog, StringComparison.Ordinal);
        Assert.Contains("DesktopText.Restore", dialog, StringComparison.Ordinal);
        Assert.Contains("DesktopText.MakeMyCopy", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("Revision ID", dialog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StateRevisionId", dialog, StringComparison.Ordinal);

        Assert.Contains("ResolveEnvironmentForStateAsync", service, StringComparison.Ordinal);
        Assert.Contains("ParentRevisionId: currentEnvironmentRevisionId", service, StringComparison.Ordinal);
        Assert.Contains("ParentRevisionId: currentStateRevisionId", service, StringComparison.Ordinal);
        Assert.Contains("EnvironmentRevisionId: restoredEnvironmentRevisionId", service, StringComparison.Ordinal);
        Assert.Contains("await _storage.StoreEnvironmentRevisionAsync(restoredEnvironment", service, StringComparison.Ordinal);
        Assert.Contains("await _storage.StoreRevisionAsync(restoredState", service, StringComparison.Ordinal);
        Assert.Contains("CurrentEnvironmentRevisionId = restoredEnvironmentRevisionId", service, StringComparison.Ordinal);
        Assert.Contains("await _storage.SaveWorldAsync(updated", service, StringComparison.Ordinal);
        Assert.Contains("EnvironmentRevisionId: environmentId", service, StringComparison.Ordinal);
        Assert.Contains("WorldId.New()", service, StringComparison.Ordinal);
        Assert.Contains("SharingMode = WorldSharingMode.LocalOnly", service, StringComparison.Ordinal);
        Assert.Contains("Visibility = WorldVisibility.Private", service, StringComparison.Ordinal);
        Assert.Contains("Description: \"Created from World History.\"", service, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryViewIsBoundedAndFailsClosedOnBrokenOrLegacyUnlinkedAuthority()
    {
        var service = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Core/Worlds/WorldHistoryService.cs"));

        Assert.Contains("DefaultMaximumEntries = 100", service, StringComparison.Ordinal);
        Assert.Contains("MaximumSupportedEntries = 500", service, StringComparison.Ordinal);
        Assert.Contains("var visited = new HashSet<RevisionId>();", service, StringComparison.Ordinal);
        Assert.Contains("has a cycle in its state history", service, StringComparison.Ordinal);
        Assert.Contains("points to missing state-history revision", service, StringComparison.Ordinal);
        Assert.Contains("HasOlderRevisions: nextRevisionId is not null", service, StringComparison.Ordinal);
        Assert.Contains("state.EnvironmentRevisionId is", service, StringComparison.Ordinal);
        Assert.Contains("points to missing environment revision", service, StringComparison.Ordinal);
        Assert.Contains("Compatibility for Worlds created before state/environment association existed", service, StringComparison.Ordinal);
        Assert.Contains("if (current.ParentRevisionId is not null)", service, StringComparison.Ordinal);
        Assert.Contains("legacy History entry", service, StringComparison.Ordinal);
        Assert.Contains("environment changed", service, StringComparison.Ordinal);
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
