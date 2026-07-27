using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class GameLibraryAttentionPresentationTests
{
    [Fact]
    public void GameAttentionProjectsExistingResponsibilityTrackerWithoutSecondStateOwner()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.GameAttention.cs"));

        Assert.Contains("var snapshot = _responsibilityTracker.Current;", source, StringComparison.Ordinal);
        Assert.Contains("snapshot.WorldId", source, StringComparison.Ordinal);
        Assert.Contains("_allWorldItems", source, StringComparison.Ordinal);
        Assert.Contains("WorldLifecycleResponsibilityKind.ActiveLifecycle", source, StringComparison.Ordinal);
        Assert.Contains("WorldLifecycleResponsibilityKind.InterruptedSession", source, StringComparison.Ordinal);
        Assert.Contains("WorldLifecycleResponsibilityKind.RecoveryNeeded", source, StringComparison.Ordinal);
        Assert.Contains("WorldLifecycleResponsibilityKind.CleanupPending", source, StringComparison.Ordinal);

        Assert.Contains("\"Preparing\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Running\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Hosting\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Saving World\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Recovery needed\"", source, StringComparison.Ordinal);
        Assert.Contains("\"Action required\"", source, StringComparison.Ordinal);

        Assert.DoesNotContain("Dictionary<WorldId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ConcurrentDictionary", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Timer", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GameAttentionReappliesFromExistingPresentationAndLibraryRefreshBoundaries()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.GameAttention.cs"));
        var startup = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.UnifiedStartup.cs"));

        var responsibilityInitialization = startup.IndexOf(
            "InitializeResponsibilityPresentation();",
            StringComparison.Ordinal);
        var attentionInitialization = startup.IndexOf(
            "InitializeGameAttentionProjection();",
            StringComparison.Ordinal);

        Assert.True(responsibilityInitialization >= 0);
        Assert.True(attentionInitialization > responsibilityInitialization);
        Assert.Contains("TextBlock.TextProperty", source, StringComparison.Ordinal);
        Assert.Contains("UIElement.VisibilityProperty", source, StringComparison.Ordinal);
        Assert.Contains("ItemsControl.ItemsSourceProperty", source, StringComparison.Ordinal);
        Assert.Contains("GameLibraryList.ItemsSource = updated;", source, StringComparison.Ordinal);
        Assert.Contains("current.SequenceEqual(updated)", source, StringComparison.Ordinal);
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
