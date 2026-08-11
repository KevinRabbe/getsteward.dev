using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PeerGameBridgeHostEndResetCompositionTests
{
    [Fact]
    public void HostPresenceEndRevokesCurrentBridgeBeforeCreatingReplacement()
    {
        var source = ReadRuntime();
        var subscribe = RequiredIndex(source, "_hostPresence.Ended += OnManagedHostPresenceEnded;");
        var handler = RequiredIndex(source, "private void OnManagedHostPresenceEnded(", subscribe);
        var clear = RequiredIndex(source, "_gameBridge = null;", handler);
        var dispose = RequiredIndex(source, "retiring?.Dispose();", clear);
        var restore = RequiredIndex(source, "RestoreGameBridge();", dispose);

        Assert.True(subscribe < handler);
        Assert.True(handler < clear);
        Assert.True(clear < dispose);
        Assert.True(dispose < restore);
    }

    [Fact]
    public void ReplacementIsCreatedOnlyOnProcessSteamDispatcher()
    {
        var source = ReadRuntime();
        var handler = RequiredIndex(source, "private void OnManagedHostPresenceEnded(");
        var access = RequiredIndex(source, "_platform.Dispatcher.CheckAccess()", handler);
        var dispatch = RequiredIndex(source, "_platform.Dispatcher.Invoke(RestoreGameBridge);", access);
        var restore = RequiredIndex(source, "private void RestoreGameBridge()", dispatch);
        var replacement = RequiredIndex(source, "replacement = new SteamPeerGameDatagramBridge(", restore);

        Assert.True(handler < access);
        Assert.True(access < dispatch);
        Assert.True(dispatch < restore);
        Assert.True(restore < replacement);
    }

    [Fact]
    public void MissingReplacementFailsClosedInsteadOfReturningRetiredBridge()
    {
        var source = ReadRuntime();
        var property = RequiredIndex(source, "public SteamPeerGameDatagramBridge GameBridge");
        var nullGuard = RequiredIndex(source, "return _gameBridge", property);
        var failure = RequiredIndex(source, "peer game bridge is unavailable after managed-host teardown", nullGuard);
        var problem = RequiredIndex(source, "_gameBridgeProblem", failure);

        Assert.True(property < nullGuard);
        Assert.True(nullGuard < failure);
        Assert.True(failure < problem);
    }

    [Fact]
    public void RuntimeUnsubscribesBeforeFinalBridgeDisposal()
    {
        var source = ReadRuntime();
        var dispose = RequiredIndex(source, "public void Dispose()");
        var unsubscribe = RequiredIndex(source, "_hostPresence.Ended -= OnManagedHostPresenceEnded;", dispose);
        var clear = RequiredIndex(source, "_gameBridge = null;", unsubscribe);
        var bridgeDispose = RequiredIndex(source, "gameBridge?.Dispose();", clear);

        Assert.True(dispose < unsubscribe);
        Assert.True(unsubscribe < clear);
        Assert.True(clear < bridgeDispose);
    }

    [Fact]
    public void ResetReliesOnExistingSingleWritableSessionGateRatherThanPacketPolling()
    {
        var source = ReadRuntime();
        var handler = RequiredIndex(source, "private void OnManagedHostPresenceEnded(");
        var gateReason = RequiredIndex(source, "ManagedWritableSessionGate permits only one writable managed host lifecycle", handler);

        Assert.True(handler < gateReason);
        Assert.DoesNotContain("ReceiveLoopAsync", source[handler..], StringComparison.Ordinal);
        Assert.DoesNotContain("AuthorizeAsync(", source[handler..], StringComparison.Ordinal);
    }

    private static string ReadRuntime()
        => File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/StewardDesktopPeerRuntime.cs"));

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
