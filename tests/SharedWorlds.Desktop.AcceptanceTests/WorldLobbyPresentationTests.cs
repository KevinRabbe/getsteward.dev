using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class WorldLobbyPresentationTests
{
    [Fact]
    public void WorldDetailsContainSmallOperationalLobbyProjection()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.WorldLobby.cs"));

        Assert.Contains("World Lobby", source, StringComparison.Ordinal);
        Assert.Contains("Playing now", source, StringComparison.Ordinal);
        Assert.Contains("World group", source, StringComparison.Ordinal);
        Assert.Contains("runtime.PlayerPresence.GetSnapshotAsync(world.Id)", source, StringComparison.Ordinal);
        Assert.Contains("— HOST", source, StringComparison.Ordinal);
        Assert.Contains("— Access Manager", source, StringComparison.Ordinal);
        Assert.Contains("WorldDetailsPanel.Children.Insert", source, StringComparison.Ordinal);
        Assert.Contains("Visibility.Collapsed", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomaticJoinPublishesOnlyAfterClientLaunchAndClearsAfterObservedEnd()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/CoordinatedJoinGameAdapter.cs"));

        var launch = source.IndexOf("await _inner.LaunchClientAsync", StringComparison.Ordinal);
        var publish = source.IndexOf("await StartPresenceAsync()", StringComparison.Ordinal);
        var observedEnd = source.IndexOf("await _inner.WaitForSessionEndAsync", StringComparison.Ordinal);
        var clear = source.IndexOf("await StopPresenceAsync()", observedEnd, StringComparison.Ordinal);

        Assert.True(launch >= 0);
        Assert.True(publish > launch);
        Assert.True(observedEnd > publish);
        Assert.True(clear > observedEnd);
        Assert.Contains("TimeSpan.FromSeconds(15)", source, StringComparison.Ordinal);
        Assert.Contains("TTL expiration is the fallback", source, StringComparison.Ordinal);
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
