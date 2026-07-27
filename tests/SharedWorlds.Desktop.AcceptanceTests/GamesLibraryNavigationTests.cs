using System.Xml.Linq;
using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class GamesLibraryNavigationTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void MainWindowStartsAtGamesLibraryBeforeWorldWorkspace()
    {
        var document = XDocument.Load(FindRepositoryFile("src/SharedWorlds.Desktop/MainWindow.xaml"));

        var gamesLibrary = FindNamedElement(document, "GamesLibraryPanel");
        var gameList = FindNamedElement(document, "GameLibraryList");
        var worldSidebar = FindNamedElement(document, "WorldSidebar");
        var worldDetails = FindNamedElement(document, "WorldDetailsScroll");
        var backToGames = FindNamedElement(document, "BackToGamesButton");

        Assert.Equal("2", (string?)gamesLibrary.Attribute("Grid.ColumnSpan"));
        Assert.Equal(
            "{x:Static local:DesktopText.GamesLibrary}",
            (string?)gameList.Attribute("AutomationProperties.Name"));
        Assert.Equal("Collapsed", (string?)worldSidebar.Attribute("Visibility"));
        Assert.Equal("Collapsed", (string?)worldDetails.Attribute("Visibility"));
        Assert.Equal(
            "{x:Static local:DesktopText.BackToGames}",
            (string?)backToGames.Attribute("Content"));
    }

    [Fact]
    public void GamesLibraryOwnsGlobalImportAndRefreshActions()
    {
        var document = XDocument.Load(FindRepositoryFile("src/SharedWorlds.Desktop/MainWindow.xaml"));
        var gamesLibrary = FindNamedElement(document, "GamesLibraryPanel");

        Assert.Contains(
            gamesLibrary.Descendants(),
            element => (string?)element.Attribute(Xaml + "Name") == "OpenImportButton");
        Assert.Contains(
            gamesLibrary.Descendants(),
            element => (string?)element.Attribute(Xaml + "Name") == "RefreshButton");
    }

    private static XElement FindNamedElement(XDocument document, string name)
        => document
            .Descendants(Presentation + "Border")
            .Concat(document.Descendants(Presentation + "ListBox"))
            .Concat(document.Descendants(Presentation + "ScrollViewer"))
            .Concat(document.Descendants(Presentation + "Button"))
            .Single(element => (string?)element.Attribute(Xaml + "Name") == name);

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
