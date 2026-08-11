using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamColdLobbyJoinCompositionTests
{
    [Fact]
    public void ColdLobbyArgumentIsProcessedOnlyAfterJoinUiIsReady()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.UnifiedStartup.cs");
        var joinUi = RequiredIndex(source, "await InitializeWorldJoinUiAsync();");
        var coldJoin = RequiredIndex(source, "InitializeSteamLobbyLaunchRequest();", joinUi);

        Assert.True(joinUi < coldJoin);
    }

    [Fact]
    public void ColdLobbyRequestConvergesOnExistingPrivateInviteHandler()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.SteamLobbyLaunch.cs");
        var parse = RequiredIndex(source, "SteamLobbyLaunchRequest.Parse(Environment.GetCommandLineArgs())");
        var peerGate = RequiredIndex(source, "if (_peerRuntime is null)", parse);
        var deferred = RequiredIndex(source, "Dispatcher.BeginInvoke(", peerGate);
        var callback = RequiredIndex(source, "PeerWorldLobbyJoinRequested(new CSteamID(lobbyId))", deferred);
        var idle = RequiredIndex(source, "DispatcherPriority.ApplicationIdle", callback);

        Assert.True(parse < peerGate);
        Assert.True(peerGate < deferred);
        Assert.True(deferred < callback);
        Assert.True(callback < idle);
        Assert.DoesNotContain("LobbyJoin.JoinAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CatchUp.RequestCatchUpAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ParserRequiresOnePositiveNumericLobbyIdAndRejectsDuplicateRequests()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.SteamLobbyLaunch.cs");
        var parser = RequiredIndex(source, "internal readonly record struct SteamLobbyLaunchRequest(");
        var argument = RequiredIndex(source, "private const string ConnectLobbyArgument = \"+connect_lobby\";", parser);
        var duplicate = RequiredIndex(source, "if (parsedLobbyId is not null)", argument);
        var parse = RequiredIndex(source, "ulong.TryParse(", duplicate);
        var invariant = RequiredIndex(source, "CultureInfo.InvariantCulture", parse);
        var nonzero = RequiredIndex(source, "lobbyId == 0", invariant);
        var consume = RequiredIndex(source, "index++;", nonzero);

        Assert.True(argument < duplicate);
        Assert.True(duplicate < parse);
        Assert.True(parse < invariant);
        Assert.True(invariant < nonzero);
        Assert.True(nonzero < consume);
        Assert.Contains("more than one +connect_lobby request", source, StringComparison.Ordinal);
        Assert.Contains("must be followed by one positive Steam lobby ID", source, StringComparison.Ordinal);
        Assert.Contains("NumberStyles.None", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ColdLobbyArgumentIsOneShotAndDoesNotBlockNormalLaunchWithoutIt()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.SteamLobbyLaunch.cs");
        var method = RequiredIndex(source, "private void InitializeSteamLobbyLaunchRequest()");
        var processed = RequiredIndex(source, "if (_steamLobbyLaunchRequestProcessed)", method);
        var mark = RequiredIndex(source, "_steamLobbyLaunchRequestProcessed = true;", processed);
        var noLobby = RequiredIndex(source, "if (parse.LobbyId is not { } lobbyId)", mark);
        var returnNoLobby = RequiredIndex(source, "return;", noLobby);

        Assert.True(processed < mark);
        Assert.True(mark < noLobby);
        Assert.True(noLobby < returnNoLobby);
    }

    [Fact]
    public void MalformedColdInviteFailsOnlyPeerInvitePresentation()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.SteamLobbyLaunch.cs");
        var problem = RequiredIndex(source, "if (parse.Problem is not null)");
        var status = RequiredIndex(source, "Steam lobby invitation could not be opened", problem);
        var returnAfterProblem = RequiredIndex(source, "return;", status);

        Assert.True(problem < status);
        Assert.True(status < returnAfterProblem);
        Assert.DoesNotContain("Application.Current.Shutdown", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Environment.Exit", source, StringComparison.Ordinal);
        Assert.DoesNotContain("throw new", source[..RequiredIndex(source, "internal readonly record struct SteamLobbyLaunchRequest(")], StringComparison.Ordinal);
    }

    private static string Read(string relativePath)
        => File.ReadAllText(FindRepositoryFile(relativePath));

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Required source fragment was not found: {value}");
        return index;
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
