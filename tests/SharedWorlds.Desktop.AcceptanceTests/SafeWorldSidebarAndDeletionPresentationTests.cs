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
    public void IncompleteSharedLocalRecordCanAbandonDurableResponsibilityAndBeRemoved()
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
        Assert.Contains("var isLocalCatalogRecord = world is not null && !_remoteWorldIds.Contains(world.Id);", deletion, StringComparison.Ordinal);
        Assert.Contains("var incompleteSharedRecord = world.SharingMode == WorldSharingMode.Shared;", deletion, StringComparison.Ordinal);
        Assert.Contains("canAbandonDurableResponsibility", deletion, StringComparison.Ordinal);
        Assert.Contains("WorldLifecycleResponsibilityKind.InterruptedSession", deletion, StringComparison.Ordinal);
        Assert.Contains("WorldLifecycleResponsibilityKind.RecoveryNeeded", deletion, StringComparison.Ordinal);
        Assert.Contains("WorldLifecycleResponsibilityKind.CleanupPending", deletion, StringComparison.Ordinal);
        Assert.Contains("Remove from Safe World", deletion, StringComparison.Ordinal);
        Assert.Contains("This does not delete a shared World for other people", deletion, StringComparison.Ordinal);
        Assert.Contains("AbandonRecoveryRecordsForRemovedWorldAsync", deletion, StringComparison.Ordinal);
        Assert.Contains("Status = WorkspaceRecoveryStatus.Abandoned", deletion, StringComparison.Ordinal);
        Assert.Contains("This record no longer owns runtime responsibility", deletion, StringComparison.Ordinal);
        Assert.Contains("_storage.DeleteWorldAsync(worldId)", deletion, StringComparison.Ordinal);
        Assert.Contains("_remoteIncompleteWorldIds.Remove(worldId);", deletion, StringComparison.Ordinal);
        Assert.Contains("await InitializeRuntimeResponsibilityAsync();", deletion, StringComparison.Ordinal);
        Assert.Contains("Other Worlds are no longer blocked by it", deletion, StringComparison.Ordinal);
        Assert.Contains("game's own save folder", deletion, StringComparison.Ordinal);

        Assert.Contains("Task<bool> DeleteWorldAsync", storageContract, StringComparison.Ordinal);
        Assert.Contains("Directory.Move(worldDirectory, tombstone);", localStorage, StringComparison.Ordinal);
        Assert.Contains(".deleting-worlds", localStorage, StringComparison.Ordinal);
        Assert.Contains("FileAttributes.ReparsePoint", localStorage, StringComparison.Ordinal);
    }

    [Fact]
    public void InterruptedSharedWorldCanReturnToSafeStateWithoutAuthorityOrEnvironmentGuessing()
    {
        var presentation = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.ResponsibilityPresentation.cs"));
        var recovery = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.InterruptedRecovery.cs"));

        Assert.Contains("WorldDetailsScroll.ScrollToTop();", presentation, StringComparison.Ordinal);
        Assert.Contains("_discardInterruptedButton.IsEnabled = !_isBusy;", presentation, StringComparison.Ordinal);
        Assert.Contains("No backend connection is required", presentation, StringComparison.Ordinal);

        var discardStart = recovery.IndexOf(
            "private async void DiscardInterruptedSessionButton_Click",
            StringComparison.Ordinal);
        var nextMethod = recovery.IndexOf(
            "private async Task<WorkspaceRecoveryRecord>",
            discardStart,
            StringComparison.Ordinal);
        Assert.True(discardStart >= 0 && nextMethod > discardStart);
        var discardBody = recovery[discardStart..nextMethod];

        Assert.DoesNotContain("HasAuthoritativeRuntimeForWorld", discardBody, StringComparison.Ordinal);
        Assert.Contains("var discard = await decision.PrepareDiscardAsync(world.Id, adapter)", discardBody, StringComparison.Ordinal);
        Assert.Contains("discard.Status == WorkspaceRecoveryStatus.CleanupPending", discardBody, StringComparison.Ordinal);
        Assert.Contains("var resolver = CreatePreparedWorldRecoveryResolver();", discardBody, StringComparison.Ordinal);
        Assert.Contains("ResolveWorkingDirectoryWithoutInstallation", discardBody, StringComparison.Ordinal);
        Assert.Contains("if (pathWithoutInstallation is null || Directory.Exists(pathWithoutInstallation))", discardBody, StringComparison.Ordinal);
        Assert.Contains("GetReadyInstallationForRecoveryRecordAsync", discardBody, StringComparison.Ordinal);
        Assert.Contains("new WorkspaceCleanupRecoveryService", discardBody, StringComparison.Ordinal);
        Assert.Contains("_workspaceRecoveryStore,\n                            resolver", discardBody, StringComparison.Ordinal);
        Assert.Contains("discard.Status == WorkspaceRecoveryStatus.Abandoned", discardBody, StringComparison.Ordinal);
        Assert.Contains("GetStorageForWorld(world)", discardBody, StringComparison.Ordinal);
        Assert.Contains("no longer blocks other Worlds", discardBody, StringComparison.Ordinal);
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
