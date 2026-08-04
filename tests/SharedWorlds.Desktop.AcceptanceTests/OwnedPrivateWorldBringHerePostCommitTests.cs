using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class OwnedPrivateWorldBringHerePostCommitTests
{
    [Fact]
    public void CancellationAndRefreshFailureNeverImplyRollbackAfterCommit()
    {
        var action = Read(
            "src/SharedWorlds.Desktop/MainWindow.OwnedPrivateWorldBringHere.cs");

        Assert.Contains(
            "StatusText.Text = result is null",
            action,
            StringComparison.Ordinal);
        Assert.Contains(
            "? \"Bring here was canceled.\"",
            action,
            StringComparison.Ordinal);
        Assert.Contains(
            "is stored on this PC. The final view refresh was canceled.",
            action,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (result is null)",
            action,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShowError(\"Could not bring private World here\", exception);",
            action,
            StringComparison.Ordinal);
        Assert.Contains(
            "is stored on this PC, but the view could not be refreshed.",
            action,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShowError(\"Private World stored, but refresh failed\", exception);",
            action,
            StringComparison.Ordinal);
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
