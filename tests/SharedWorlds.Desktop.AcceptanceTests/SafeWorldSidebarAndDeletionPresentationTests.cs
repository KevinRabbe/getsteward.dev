using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SafeWorldSidebarAndDeletionPresentationTests
{
    [Fact]
    public void SelectedGameSidebarUsesDedicatedSearchAndWorldRowsWithoutOverlayLayout()
    {
        var home = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.SafeWorldGamesHome.cs"));
        var search = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.WorldSearch.cs"));

        Assert.Contains("RebuildSafeWorldSelectedGameSidebar();", home, StringComparison.Ordinal);
        Assert.Contains("DetachSafeWorldSidebarElement(_worldSearchBox);", home, StringComparison.Ordinal);
        Assert.Contains("DetachSafeWorldSidebarElement(WorldList);", home, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(_worldSearchBox, 1);", home, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(WorldList, 2);", home, StringComparison.Ordinal);
        Assert.Contains("WorldSidebar.Child = layout;", home, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer.SetHorizontalScrollBarVisibility(WorldList, ScrollBarVisibility.Disabled);", home, StringComparison.Ordinal);

        Assert.DoesNotContain("Panel.SetZIndex(_worldSearchBox, 1)", search, StringComparison.Ordinal);
        Assert.DoesNotContain("worldSidebarGrid.Children.Add(_worldSearchBox)", search, StringComparison.Ordinal);
        Assert.DoesNotContain("margin.Top + 42", search, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteWorldIsLocalOnlyConfirmedAndUsesStorageBoundary()
    {
        var deletion = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.WorldDeletion.cs"));
        var startup = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.UnifiedStartup.cs"));
        var storageContract = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Core/Abstractions/IWorldStorage.cs"));
        var localStorage = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Infrastructure/Storage/LocalWorldStorage.cs"));

        Assert.Contains("InitializeWorldDeletionUi();", startup, StringComparison.Ordinal);
        Assert.Contains("DesktopText.DeleteWorld", deletion, StringComparison.Ordinal);
        Assert.Contains("WorldSharingMode.LocalOnly", deletion, StringComparison.Ordinal);
        Assert.Contains("!_remoteWorldIds.Contains(world.Id)", deletion, StringComparison.Ordinal);
        Assert.Contains("MessageBoxButton.YesNo", deletion, StringComparison.Ordinal);
        Assert.Contains("MessageBoxImage.Warning", deletion, StringComparison.Ordinal);
        Assert.Contains("_storage.DeleteWorldAsync(worldId)", deletion, StringComparison.Ordinal);
        Assert.Contains("game's own save folder", deletion, StringComparison.Ordinal);

        Assert.Contains("Task<bool> DeleteWorldAsync", storageContract, StringComparison.Ordinal);
        Assert.Contains("Directory.Move(worldDirectory, tombstone);", localStorage, StringComparison.Ordinal);
        Assert.Contains(".deleting-worlds", localStorage, StringComparison.Ordinal);
        Assert.Contains("FileAttributes.ReparsePoint", localStorage, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteWorldVocabularyUsesNeutralResourceFallback()
    {
        Assert.Equal("Delete World", DesktopText.DeleteWorld);
        Assert.False(string.IsNullOrWhiteSpace(DesktopText.DeleteWorldDescription));
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
