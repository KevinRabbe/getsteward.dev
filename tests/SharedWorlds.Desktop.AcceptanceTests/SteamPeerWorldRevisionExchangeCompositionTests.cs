using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamPeerWorldRevisionExchangeCompositionTests
{
    [Fact]
    public void ExchangeUsesNetworkingSocketsWithoutOwningSteamLifetime()
    {
        var source = ReadExchange();

        Assert.Contains("SteamNetworkingSockets.CreateListenSocketP2P(", source, StringComparison.Ordinal);
        Assert.Contains("SteamNetworkingSockets.ConnectP2P(", source, StringComparison.Ordinal);
        Assert.Contains("SteamNetworkingSockets.SendMessageToConnection(", source, StringComparison.Ordinal);
        Assert.Contains("SteamNetworkingSockets.ReceiveMessagesOnConnection(", source, StringComparison.Ordinal);
        Assert.Contains("SteamNetworkingUtils.InitRelayNetworkAccess();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamAPI.Init", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamAPI.Shutdown", source, StringComparison.Ordinal);
    }

    [Fact]
    public void IncomingOfferMustMatchConfirmedLobbyHandoffBeforeReady()
    {
        var source = ReadExchange();
        var authorize = RequiredIndex(source, "private async Task AuthorizeIncomingOfferAsync(");
        var snapshot = RequiredIndex(source, "var snapshot = await _lobby.GetAsync(offer.World.Id);", authorize);
        var confirmed = RequiredIndex(source, "!snapshot.OwnerConfirmed", snapshot);
        var remoteHost = RequiredIndex(source, "!SameSteamUser(snapshot.Owner, context.RemoteSteamId)", confirmed);
        var requested = RequiredIndex(source, "snapshot.RequestedHost is null", remoteHost);
        var localTarget = RequiredIndex(
            source,
            "!SameSteamUser(snapshot.RequestedHost, _platform.LocalSteamId.m_SteamID)",
            requested);
        var ready = RequiredIndex(source, "MessageKind.Ready", localTarget);

        Assert.True(snapshot < confirmed);
        Assert.True(confirmed < remoteHost);
        Assert.True(remoteHost < requested);
        Assert.True(requested < localTarget);
        Assert.True(localTarget < ready);
    }

    [Fact]
    public void PayloadQueueIsBoundedAndNeverConfiguredToDropWorldBytes()
    {
        var source = ReadExchange();

        Assert.Contains("Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaxQueuedPayloadChunks)", source, StringComparison.Ordinal);
        Assert.Contains("FullMode = BoundedChannelFullMode.Wait", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BoundedChannelFullMode.DropWrite", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BoundedChannelFullMode.DropOldest", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BoundedChannelFullMode.DropNewest", source, StringComparison.Ordinal);
        Assert.Contains("if (!incoming.Chunks!.Writer.TryWrite(chunk))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DiskSpoolAndInstallRunOffTheWpfDispatcher()
    {
        var source = ReadExchange();

        Assert.Contains("incoming.WriterTask = Task.Run(() => WriteIncomingChunksAsync(incoming));", source, StringComparison.Ordinal);
        Assert.Contains("_ = Task.Run(() => FinishIncomingAsync(context));", source, StringComparison.Ordinal);
        Assert.Contains("FileOptions.DeleteOnClose", source, StringComparison.Ordinal);
        Assert.Contains("FileOptions.Asynchronous", source, StringComparison.Ordinal);
    }

    [Fact]
    public void NetworkMessagesAreReliableBoundedAndAlwaysReleased()
    {
        var source = ReadExchange();

        Assert.Contains("private const int PayloadChunkBytes = 64 * 1024;", source, StringComparison.Ordinal);
        Assert.Contains("private const int MaxOfferBytes = 256 * 1024;", source, StringComparison.Ordinal);
        Assert.Contains("Constants.k_nSteamNetworkingSend_ReliableNoNagle", source, StringComparison.Ordinal);
        Assert.Contains("EResult.k_EResultLimitExceeded", source, StringComparison.Ordinal);
        Assert.Contains("SteamNetworkingMessage_t.Release(pointer);", source, StringComparison.Ordinal);
        Assert.Contains("finally", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlMessagesAreBoundToOneProtocolAndTransferId()
    {
        var source = ReadExchange();

        Assert.Contains("private const int ProtocolVersion = 1;", source, StringComparison.Ordinal);
        Assert.Contains("actualTransferId != expectedTransferId", source, StringComparison.Ordinal);
        Assert.Contains("protocolVersion != ProtocolVersion", source, StringComparison.Ordinal);
        Assert.Contains("MessageKind.Receipt", source, StringComparison.Ordinal);
        Assert.Contains("MessageKind.Reject", source, StringComparison.Ordinal);
    }

    private static string ReadExchange()
        => File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/SteamPeerWorldRevisionExchange.cs"));

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
