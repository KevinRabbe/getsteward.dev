using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class TopLevelNavigationAvailabilityTests
{
    [Fact]
    public void BusyWorldWorkCannotDisableTopLevelDestinations()
    {
        var navigation = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.NavigationV2State.cs"));

        Assert.Contains("_gamesNavigationButton.IsEnabled = true;", navigation, StringComparison.Ordinal);
        Assert.Contains("_globalLobbyButton.IsEnabled = true;", navigation, StringComparison.Ordinal);
        Assert.Contains("_globalSettingsButton.IsEnabled = true;", navigation, StringComparison.Ordinal);
        Assert.Contains("RefreshButton.IsEnabledChanged += (_, _) => KeepTopLevelNavigationAvailable();", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("= RefreshButton.IsEnabled;", navigation, StringComparison.Ordinal);
    }

    [Fact]
    public void GuardedQuitAlwaysHasAnExplicitExitAndPreservesRecoveryWhenAvailable()
    {
        var tray = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.Tray.cs"));
        var guardedQuit = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.GuardedQuit.cs"));
        var dialog = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/GuardedQuitDialog.cs"));
        var tracker = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Core/Worlds/WorldLifecycleResponsibilityTracker.cs"));

        Assert.Contains("private async void RequestQuitSteward()", tray, StringComparison.Ordinal);
        Assert.Contains("ConfirmGuardedQuitAsync(responsibility)", tray, StringComparison.Ordinal);
        Assert.Contains("CompleteExplicitQuit();", tray, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageBox.Show(", tray, StringComparison.Ordinal);

        Assert.Contains("await _workspaceRecoveryStore.ListAsync()", guardedQuit, StringComparison.Ordinal);
        Assert.Contains("records.Any(record => record.WorldId == responsibility.WorldId)", guardedQuit, StringComparison.Ordinal);
        Assert.Contains("OpenGameWorkspace(", guardedQuit, StringComparison.Ordinal);
        Assert.Contains("WorldDetailsScroll.ScrollToTop();", guardedQuit, StringComparison.Ordinal);
        Assert.Contains("new GuardedQuitDialog(", guardedQuit, StringComparison.Ordinal);
        Assert.DoesNotContain("_workspaceRecoveryStore.RemoveAsync", guardedQuit, StringComparison.Ordinal);
        Assert.DoesNotContain("_responsibilityTracker.InitializeFromRecoveryRecords", guardedQuit, StringComparison.Ordinal);

        Assert.Contains("Recovery evidence is safely stored", dialog, StringComparison.Ordinal);
        Assert.Contains("I have closed the game", dialog, StringComparison.Ordinal);
        Assert.Contains("Quit and recover next time", dialog, StringComparison.Ordinal);
        Assert.Contains("Force quit without recovery", dialog, StringComparison.Ordinal);
        Assert.Contains("changes from this unresolved session may be lost", dialog, StringComparison.Ordinal);
        Assert.Contains("_quitButton.IsEnabled = _gameClosed && _lossAcknowledged", dialog, StringComparison.Ordinal);

        Assert.Contains("case WorkspaceRecoveryStatus.Active:", tracker, StringComparison.Ordinal);
        Assert.Contains("_kind = WorldLifecycleResponsibilityKind.InterruptedSession;", tracker, StringComparison.Ordinal);
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
