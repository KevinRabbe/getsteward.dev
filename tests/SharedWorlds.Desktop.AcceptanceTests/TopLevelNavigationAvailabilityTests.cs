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
    public void GuardedQuitReturnsUserToResponsibleWorldInsteadOfDeadEndWarning()
    {
        var tray = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.Tray.cs"));

        Assert.Contains("OpenStewardWindow();", tray, StringComparison.Ordinal);
        Assert.Contains("item.World.Id == responsibility.WorldId", tray, StringComparison.Ordinal);
        Assert.Contains("OpenGameWorkspace(", tray, StringComparison.Ordinal);
        Assert.Contains("Finish, stop and save, or recover that session before quitting.", tray, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Resolve or safely finish it before quitting.",
            tray,
            StringComparison.Ordinal);
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
