using System.Globalization;
using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class GameTechnicalReadinessPresentationTests
{
    [Fact]
    public void GameWorkspaceSummaryProjectsExistingCapabilityTruthOnly()
    {
        var source = File.ReadAllText(
            FindRepositoryFile("src/SharedWorlds.Desktop/MainWindow.GameTechnicalReadiness.cs"));

        Assert.Contains("GameAdapterCapabilities.AutomaticLocalLaunch", source, StringComparison.Ordinal);
        Assert.Contains("GameAdapterCapabilities.AutomaticHostLaunch", source, StringComparison.Ordinal);
        Assert.Contains("GameAdapterCapabilities.AutomaticClientJoin", source, StringComparison.Ordinal);
        Assert.Contains("GameAdapterCapabilities.AutomaticHostStop", source, StringComparison.Ordinal);
        Assert.Contains("GameAdapterCapabilities.NativeWorldCreation", source, StringComparison.Ordinal);

        Assert.DoesNotContain("DiscoverInstallationsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("VerifyEnvironmentAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Factorio", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Palworld", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SevenDays", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProjectZomboid", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TechnicalReadinessVocabularyUsesNeutralResourceFallback()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");

            var required = new[]
            {
                DesktopText.TechnicalReadiness,
                DesktopText.WorldStateImportSupported,
                DesktopText.AdapterSupportsFormat,
                DesktopText.NoManagedPlayActions,
                DesktopText.CreateWorld
            };

            Assert.All(required, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void MainWindowInitializesTheReadinessProjection()
    {
        var source = File.ReadAllText(
            FindRepositoryFile("src/SharedWorlds.Desktop/MainWindow.xaml.cs"));

        Assert.Contains("InitializeGameTechnicalReadinessUi();", source, StringComparison.Ordinal);
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
