using System.Globalization;
using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class LocalizationReadinessTests
{
    [Fact]
    public void FixedFirstReleaseVocabularyResolvesThroughNeutralResourceFallback()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");

            var required = new[]
            {
                DesktopText.Games,
                DesktopText.GamesLibrary,
                DesktopText.BackToGames,
                DesktopText.Worlds,
                DesktopText.Lobby,
                DesktopText.Settings,
                DesktopText.SearchWorlds,
                DesktopText.WorldLobby,
                DesktopText.PlayingNow,
                DesktopText.WorldGroup,
                DesktopText.DangerZone,
                DesktopText.Preparing,
                DesktopText.Running,
                DesktopText.Hosting,
                DesktopText.SavingWorld,
                DesktopText.RecoveryNeeded,
                DesktopText.ActionRequired,
                DesktopText.StartWorld,
                DesktopText.HostWorld,
                DesktopText.Join,
                DesktopText.ShareWorld,
                DesktopText.ManageAccess,
                DesktopText.StopAndSave
            };

            Assert.All(required, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void ShellAndRecentPresentationSlicesUseDesktopTextForFixedVocabulary()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("src/SharedWorlds.Desktop/MainWindow.xaml"));
        var search = File.ReadAllText(FindRepositoryFile("src/SharedWorlds.Desktop/MainWindow.WorldSearch.cs"));
        var controls = File.ReadAllText(FindRepositoryFile("src/SharedWorlds.Desktop/MainWindow.CompletenessControls.cs"));
        var creation = File.ReadAllText(FindRepositoryFile("src/SharedWorlds.Desktop/MainWindow.WorldCreation.cs"));
        var shell = File.ReadAllText(FindRepositoryFile("src/SharedWorlds.Desktop/MainWindow.ProductShell.cs"));
        var attention = File.ReadAllText(FindRepositoryFile("src/SharedWorlds.Desktop/MainWindow.GameAttention.cs"));
        var responsibility = File.ReadAllText(FindRepositoryFile("src/SharedWorlds.Desktop/MainWindow.ResponsibilityPresentation.cs"));
        var deletion = File.ReadAllText(FindRepositoryFile("src/SharedWorlds.Desktop/MainWindow.WorldDeletion.cs"));

        Assert.Contains("{x:Static local:DesktopText.Tagline}", xaml, StringComparison.Ordinal);
        Assert.Contains("{x:Static local:DesktopText.Games}", xaml, StringComparison.Ordinal);
        Assert.Contains("{x:Static local:DesktopText.BackToGames}", xaml, StringComparison.Ordinal);
        Assert.Contains("{x:Static local:DesktopText.Worlds}", xaml, StringComparison.Ordinal);
        Assert.Contains("{x:Static local:DesktopText.Play}", xaml, StringComparison.Ordinal);
        Assert.Contains("{x:Static local:DesktopText.WorldSettings}", xaml, StringComparison.Ordinal);
        Assert.Contains("{x:Static local:DesktopText.EnvironmentReadiness}", xaml, StringComparison.Ordinal);
        Assert.Contains("{x:Static local:DesktopText.TechnicalDetails}", xaml, StringComparison.Ordinal);
        Assert.Contains("{x:Static local:DesktopText.Ready}", xaml, StringComparison.Ordinal);

        Assert.Contains("DesktopText.SearchWorlds", search, StringComparison.Ordinal);
        Assert.Contains("DesktopText.NoWorldSearchMatches", search, StringComparison.Ordinal);
        Assert.Contains("DesktopText.Settings", controls, StringComparison.Ordinal);
        Assert.Contains("DesktopText.HostingOnThisDevice", controls, StringComparison.Ordinal);
        Assert.Contains("DesktopText.CreateWorld", creation, StringComparison.Ordinal);
        Assert.Contains("DesktopText.Lobby", shell, StringComparison.Ordinal);
        Assert.Contains("DesktopText.PlayingNow", shell, StringComparison.Ordinal);
        Assert.Contains("DesktopText.WorldGroup", shell, StringComparison.Ordinal);
        Assert.Contains("DesktopText.HostStartingSuffix", shell, StringComparison.Ordinal);
        Assert.Contains("DesktopText.AccessManagerSuffix", shell, StringComparison.Ordinal);
        Assert.Contains("DesktopText.DangerZone", deletion, StringComparison.Ordinal);
        Assert.Contains("DesktopText.Preparing", attention, StringComparison.Ordinal);
        Assert.Contains("DesktopText.Hosting", attention, StringComparison.Ordinal);
        Assert.Contains("DesktopText.SavingWorld", attention, StringComparison.Ordinal);
        Assert.Contains("DesktopText.RecoveryNeeded", responsibility, StringComparison.Ordinal);
        Assert.Contains("DesktopText.ActionRequired", responsibility, StringComparison.Ordinal);
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
