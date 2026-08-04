using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class OwnedPrivateWorldBringHereDesktopActionTests
{
    [Fact]
    public void StartupInitializesActionAfterCatalogHooks()
    {
        var startup = Read("src/SharedWorlds.Desktop/MainWindow.UnifiedStartup.cs");

        var catalogHooks = RequiredIndex(
            startup,
            "InitializeOwnedPrivateWorldCatalogRefreshHooks();");
        var action = RequiredIndex(
            startup,
            "InitializeOwnedPrivateWorldBringHereAction();",
            catalogHooks);

        Assert.True(catalogHooks < action);
        Assert.Equal(
            1,
            CountOccurrences(
                startup,
                "InitializeOwnedPrivateWorldBringHereAction();"));
    }

    [Fact]
    public void ButtonIsAccessibleAndLivesOnExistingRemoteDetailPanel()
    {
        var action = Read(
            "src/SharedWorlds.Desktop/MainWindow.OwnedPrivateWorldBringHere.cs");

        Assert.Contains(
            "var panel = _ownedPrivateWorldDetailsPanel",
            action,
            StringComparison.Ordinal);
        Assert.Contains(
            "Content = \"Bring here\"",
            action,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationProperties.SetName(",
            action,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"Bring private World to this PC\"",
            action,
            StringComparison.Ordinal);
        Assert.Contains(
            "panel.Children.Add(_ownedPrivateWorldBringHereButton);",
            action,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ActionRequiresAvailableOneSourceNoConflictsAndAuthenticatedRuntime()
    {
        var action = Read(
            "src/SharedWorlds.Desktop/MainWindow.OwnedPrivateWorldBringHere.cs");
        var stateStart = RequiredIndex(
            action,
            "private void UpdateOwnedPrivateWorldBringHereActionState()");
        var state = action[stateStart..];

        Assert.Contains(
            "Availability: BringHereAvailability.Available",
            state,
            StringComparison.Ordinal);
        Assert.Contains("Source: not null", state, StringComparison.Ordinal);
        Assert.Contains(
            "entry.ConflictingClaims.Count == 0",
            state,
            StringComparison.Ordinal);
        Assert.Contains("!_isBusy", state, StringComparison.Ordinal);
        Assert.Contains(
            "Volatile.Read(ref _remoteRuntime) is not null",
            state,
            StringComparison.Ordinal);
        Assert.Contains(
            "RefreshButton.IsEnabled",
            state,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ClickRunsExactRuntimeThenRefreshesCanonicalAndRemoteProjection()
    {
        var action = Read(
            "src/SharedWorlds.Desktop/MainWindow.OwnedPrivateWorldBringHere.cs");
        var methodStart = RequiredIndex(
            action,
            "OwnedPrivateWorldBringHereButton_Click(");
        var methodEnd = RequiredIndex(
            action,
            "private void UpdateOwnedPrivateWorldBringHereActionState()",
            methodStart);
        var method = action[methodStart..methodEnd];

        var oneActive = RequiredIndex(
            method,
            "Interlocked.CompareExchange(");
        var busy = RequiredIndex(method, "SetBusy(true);", oneActive);
        var runtime = RequiredIndex(
            method,
            "await remote.BringOwnedPrivateWorldHereAsync(",
            busy);
        var clearRemoteSelection = RequiredIndex(
            method,
            "ShowGamesLibraryFromOwnedPrivateWorld();",
            runtime);
        var refreshLocal = RequiredIndex(
            method,
            "await RefreshUnifiedWorldsAsync(",
            clearRemoteSelection);
        var refreshCatalog = RequiredIndex(
            method,
            "await RefreshOwnedPrivateWorldCatalogAsync(",
            refreshLocal);

        Assert.True(oneActive < busy);
        Assert.True(busy < runtime);
        Assert.True(runtime < clearRemoteSelection);
        Assert.True(clearRemoteSelection < refreshLocal);
        Assert.True(refreshLocal < refreshCatalog);
        Assert.Contains(
            "item.Entry.WorldId,\n                cancellation.Token",
            method,
            StringComparison.Ordinal);
        Assert.Contains(
            "item.Entry.WorldId,\n                preserveStatus: true",
            method,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ActionCancelsOnCloseUsesDiagnosticsAndNeverLaunchesWorld()
    {
        var action = Read(
            "src/SharedWorlds.Desktop/MainWindow.OwnedPrivateWorldBringHere.cs");

        Assert.Contains(
            "Closed += (_, _) =>",
            action,
            StringComparison.Ordinal);
        Assert.Contains(
            "_ownedPrivateWorldBringHereCancellation)?.Cancel();",
            action,
            StringComparison.Ordinal);
        Assert.Contains(
            "catch (OperationCanceledException) when (cancellation.IsCancellationRequested)",
            action,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShowError(\"Could not bring private World here\", exception);",
            action,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ContinueLocalAsync", action, StringComparison.Ordinal);
        Assert.DoesNotContain("ContinueAsHostAsync", action, StringComparison.Ordinal);
        Assert.DoesNotContain("CoordinateManagedHost", action, StringComparison.Ordinal);
        Assert.DoesNotContain("GetLifecycleForWorld", action, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", action, StringComparison.Ordinal);
    }

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Required source fragment was not found: {value}");
        return index;
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string Read(string relativePath)
        => File.ReadAllText(FindRepositoryFile(relativePath));

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
