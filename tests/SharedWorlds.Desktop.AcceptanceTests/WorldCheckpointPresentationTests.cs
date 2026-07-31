using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class WorldCheckpointPresentationTests
{
    [Fact]
    public void CheckpointNamesBecomeHistoryTitlesWithoutTechnicalIds()
    {
        var dialog = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/WorldHistoryDialog.cs"));

        Assert.Contains("entry.Checkpoint?.Name", dialog, StringComparison.Ordinal);
        Assert.Contains("Text = titleText", dialog, StringComparison.Ordinal);
        Assert.Contains("Text = detailText", dialog, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(item, $\"{titleText}, {detailText}\")", dialog, StringComparison.Ordinal);
        Assert.Contains("DesktopText.Checkpoint", dialog, StringComparison.Ordinal);
        Assert.Contains("DesktopText.CurrentState", dialog, StringComparison.Ordinal);
        Assert.Contains("DesktopText.EarlierSave", dialog, StringComparison.Ordinal);
        Assert.Contains("DesktopText.NameCheckpoint", dialog, StringComparison.Ordinal);
        Assert.Contains("DesktopText.RenameCheckpoint", dialog, StringComparison.Ordinal);
        Assert.Contains("DesktopText.RemoveCheckpoint", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("Revision ID", dialog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("entry.Revision.Id", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("entry.Checkpoint?.StateRevisionId", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("StateRevisionId.ToString", dialog, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckpointActionsRemainSeparateFromStateActions()
    {
        var dialog = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/WorldHistoryDialog.cs"));
        var window = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.WorldHistory.cs"));

        Assert.Contains("var checkpointActions = new StackPanel", dialog, StringComparison.Ordinal);
        Assert.Contains("var mainActions = new StackPanel", dialog, StringComparison.Ordinal);
        Assert.Contains("WorldHistoryDialogAction.NameCheckpoint", window, StringComparison.Ordinal);
        Assert.Contains("WorldHistoryDialogAction.RemoveCheckpoint", window, StringComparison.Ordinal);
        Assert.Contains("WorldCheckpointService", window, StringComparison.Ordinal);
        Assert.Contains("Only the label will be removed", window, StringComparison.Ordinal);
        Assert.Contains("The saved state remains in World History", window, StringComparison.Ordinal);
    }

    [Fact]
    public void NamingDialogExplainsCheckpointDoesNotCopyWorld()
    {
        var dialog = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/CheckpointNameDialog.cs"));

        Assert.Contains("does not create another copy of the World", dialog, StringComparison.Ordinal);
        Assert.Contains("WorldCheckpointService.MaximumCheckpointNameLength", dialog, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetName(_nameBox, DesktopText.CheckpointName)", dialog, StringComparison.Ordinal);
        Assert.Contains("_saveButton.IsEnabled = !string.IsNullOrWhiteSpace", dialog, StringComparison.Ordinal);
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
