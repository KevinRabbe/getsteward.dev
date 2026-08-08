using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamPlatformLifetimeCompositionTests
{
    [Fact]
    public void AppOwnsExactlyOneLongLivedSteamRuntime()
    {
        var app = Read("src/SharedWorlds.Desktop/App.xaml.cs");
        var platform = Read("src/SharedWorlds.Desktop/SteamPlatformRuntime.cs");

        Assert.Contains(
            "private SteamPlatformRuntime? _steamPlatformRuntime;",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal bool TryGetOrCreateSteamPlatformRuntime(",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "_steamPlatformRuntime?.Dispose();",
            app,
            StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(platform, "SteamAPI.Init()"));
        Assert.Equal(2, CountOccurrences(platform, "SteamAPI.Shutdown()"));
        Assert.Contains("SteamAPI.RunCallbacks();", platform, StringComparison.Ordinal);
        Assert.Contains("_callbackTimer.Start();", platform, StringComparison.Ordinal);
    }

    [Fact]
    public void TicketSourceReusesPlatformAndNeverOwnsSteamShutdown()
    {
        var tickets = Read("src/SharedWorlds.Desktop/SteamWebApiTicketSource.cs");
        var authentication = Read("src/SharedWorlds.Desktop/MainWindow.RemoteAuthentication.cs");

        Assert.Contains(
            "public SteamWebApiTicketSource(SteamPlatformRuntime platform)",
            tickets,
            StringComparison.Ordinal);
        Assert.Contains("_platform.LocalUser", tickets, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamAPI.Init", tickets, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamAPI.Shutdown", tickets, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamAPI.RunCallbacks", tickets, StringComparison.Ordinal);
        Assert.Contains(
            "app.TryGetOrCreateSteamPlatformRuntime(",
            authentication,
            StringComparison.Ordinal);
        Assert.Contains(
            "new SteamWebApiTicketSource(steamPlatform!)",
            authentication,
            StringComparison.Ordinal);
    }

    private static string Read(string relativePath)
        => File.ReadAllText(FindRepositoryFile(relativePath));

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
