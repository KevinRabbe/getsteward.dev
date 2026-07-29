using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class WorldWorkspaceCompletenessTests
{
    [Fact]
    public void ExistingWorldSearchRemainsSingleOwnerWithoutPermanentSortChrome()
    {
        var startup = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.UnifiedStartup.cs"));
        var search = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.WorldSearch.cs"));
        var completeness = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.CompletenessControls.cs"));

        Assert.Contains("InitializeWorldSearchUi();", startup, StringComparison.Ordinal);
        Assert.Contains("private readonly TextBox _worldSearchBox", search, StringComparison.Ordinal);
        Assert.DoesNotContain("private TextBox? _worldSearchBox", completeness, StringComparison.Ordinal);
        Assert.Contains("InitializeWorldSearchUi();", completeness, StringComparison.Ordinal);

        Assert.DoesNotContain("InitializeWorldWorkspaceSort", completeness, StringComparison.Ordinal);
        Assert.DoesNotContain("new ComboBox", completeness, StringComparison.Ordinal);
        Assert.DoesNotContain("SortDescriptions", completeness, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectedGameKeepsOnlyContextualCreateAndAddWorldChrome()
    {
        var home = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.SafeWorldGamesHome.cs"));
        var creation = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.WorldCreation.cs"));
        var dialog = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/CreateWorldDialog.cs"));

        Assert.Contains("gameHeader.Children.Remove(RefreshGameButton);", home, StringComparison.Ordinal);
        Assert.Contains("gameHeader.Children.Add(OpenImportButton);", home, StringComparison.Ordinal);
        Assert.Contains("gameHeader.Children.Add(_createWorldButton);", home, StringComparison.Ordinal);

        Assert.Contains("_selectedGameAdapterId is { } adapterId", creation, StringComparison.Ordinal);
        Assert.Contains("GameAdapterCapabilities.NativeWorldCreation", creation, StringComparison.Ordinal);
        Assert.Contains("_createWorldButton.Visibility = supported ? Visibility.Visible : Visibility.Collapsed", creation, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (var adapter in _registeredGameAdapters.Values", creation, StringComparison.Ordinal);

        Assert.Contains("if (options.Count == 1)", dialog, StringComparison.Ordinal);
        Assert.Contains("gamePresentation = new Border", dialog, StringComparison.Ordinal);
        Assert.Contains("Safe World then keeps its own managed copy", dialog, StringComparison.Ordinal);
    }

    [Fact]
    public void GamesHomeCannotBeOverpaintedByResponsiveWorldWorkspace()
    {
        var responsive = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.ResponsiveWorkspace.cs"));
        var app = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/App.xaml.cs"));

        const string homeGuard = "if (_selectedGameAdapterId is null)";
        var guardStart = responsive.IndexOf(homeGuard, StringComparison.Ordinal);
        Assert.True(guardStart >= 0, "Responsive workspace must explicitly recognize the games-home state.");

        var nextLayoutBranch = responsive.IndexOf("if (!narrow)", guardStart, StringComparison.Ordinal);
        Assert.True(nextLayoutBranch > guardStart, "Games-home handling must happen before selected-game responsive layout.");

        var gamesHomeLayout = responsive[guardStart..nextLayoutBranch];
        Assert.Contains("WorldSidebar.Visibility = Visibility.Collapsed;", gamesHomeLayout, StringComparison.Ordinal);
        Assert.Contains("WorldDetailsScroll.Visibility = Visibility.Collapsed;", gamesHomeLayout, StringComparison.Ordinal);
        Assert.Contains("BackToWorldsButton.Visibility = Visibility.Collapsed;", gamesHomeLayout, StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility.Visible", gamesHomeLayout, StringComparison.Ordinal);

        Assert.Contains("Title = $\"Safe World {StewardBuildVersion.Current}\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Title = $\"Steward ", app, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalSettingsRehomesExistingDevicePreferenceInsteadOfDuplicatingPersistence()
    {
        var completeness = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.CompletenessControls.cs"));
        var hostingPreference = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.UnifiedHostingPreference.cs"));

        Assert.Contains("Content = DesktopText.Settings", completeness, StringComparison.Ordinal);
        Assert.Contains("sidebarGrid.Children.Remove(existingSettings);", completeness, StringComparison.Ordinal);
        Assert.Contains("content.Children.Add(existingSettings);", completeness, StringComparison.Ordinal);
        Assert.Contains("existingSettings.Header = DesktopText.HostingOnThisDevice", completeness, StringComparison.Ordinal);
        Assert.DoesNotContain("new CheckBox", completeness, StringComparison.Ordinal);

        Assert.Contains("AllowHostingCheckBox.Click += UnifiedAllowHostingCheckBox_Click", hostingPreference, StringComparison.Ordinal);
        Assert.Contains("_deviceSettingsStore.SaveAsync(updated)", hostingPreference, StringComparison.Ordinal);
        Assert.Contains("HostingPreferenceExplicit = true", hostingPreference, StringComparison.Ordinal);
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
