using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class WorldWorkspaceCompletenessTests
{
    [Fact]
    public void ExistingWorldSearchRemainsSingleOwnerAndSortComposesOnItsView()
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
        Assert.Contains("CollectionViewSource.GetDefaultView(WorldList.ItemsSource)", completeness, StringComparison.Ordinal);
        Assert.Contains("view.SortDescriptions.Clear()", completeness, StringComparison.Ordinal);
        Assert.Contains("nameof(UnifiedWorldListItem.Name)", completeness, StringComparison.Ordinal);
        Assert.Contains("ListSortDirection.Descending", completeness, StringComparison.Ordinal);
        Assert.Contains("ListSortDirection.Ascending", completeness, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalSettingsRehomesExistingDevicePreferenceInsteadOfDuplicatingPersistence()
    {
        var completeness = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.CompletenessControls.cs"));
        var hostingPreference = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.UnifiedHostingPreference.cs"));

        Assert.Contains("Content = \"Settings\"", completeness, StringComparison.Ordinal);
        Assert.Contains("sidebarGrid.Children.Remove(existingSettings);", completeness, StringComparison.Ordinal);
        Assert.Contains("content.Children.Add(existingSettings);", completeness, StringComparison.Ordinal);
        Assert.Contains("Hosting on this device", completeness, StringComparison.Ordinal);
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
