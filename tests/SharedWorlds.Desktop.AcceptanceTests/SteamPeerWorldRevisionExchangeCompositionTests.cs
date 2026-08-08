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
    public void OneSocketEngineImplementsBootstrapAndHandoffTransfers()
    {
        var source = ReadExchange();

        Assert.Contains("IPeerWorldRevisionExchange,", source, StringComparison.Ordinal);
        Assert.Contains("IPeerWorldBootstrapExchange,", source, StringComparison.Ordinal);
        Assert.Contains("TransferPurpose.HandoffRevision", source, StringComparison.Ordinal);
        Assert.Contains("TransferPurpose.Bootstrap", source, StringComparison.Ordinal);
        Assert.Contains("await _revisionInstaller.InstallAsync(", source, StringComparison.Ordinal);
        Assert.Contains("await _bootstrapInstaller.InstallAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void IncomingHandoffMustMatchConfirmedLobbyTargetBeforeReady()
    {
        var source = ReadExchange();
        var authorize = RequiredIndex(source, "private async Task AuthorizeIncomingOfferAsync(");
        var snapshot = RequiredIndex(source, "var snapshot = await _lobby.GetAsync(offer.World.Id);", authorize);
        var confirmed = RequiredIndex(source, "!snapshot.OwnerConfirmed", snapshot);
        var remoteHost = RequiredIndex(source, "!SameSteamUser(snapshot.Owner, context.RemoteSteamId)", confirmed);
        var handoffPurpose = RequiredIndex(source, "case TransferPurpose.HandoffRevision:", remoteHost);
        var requested = RequiredIndex(source, "snapshot.RequestedHost is null", handoffPurpose);
        var localTarget = RequiredIndex(
            source,
            "snapshot.RequestedHost,\n                            _platform.LocalSteamId.m_SteamID",
            requested);
        var ready = RequiredIndex(source, "MessageKind.Ready", localTarget);

        Assert.True(snapshot < confirmed);
        Assert.True(confirmed < remoteHost);
        Assert.True(remoteHost < handoffPurpose);
        Assert.True(handoffPurpose < requested);
        Assert.True(requested < localTarget);
        Assert.True(localTarget < ready);
    }

    [Fact]
    public void BootstrapRequiresConfirmedHostNoHandoffAndCanonicalLocalMembership()
    {
        var source = ReadExchange();
        var bootstrap = RequiredIndex(source, "case TransferPurpose.Bootstrap:");
        var noHandoff = RequiredIndex(source, "if (snapshot.RequestedHost is not null)", bootstrap);
        var membership = RequiredIndex(
            source,
            "PeerWorldBootstrapTransferService.ContainsStableMember(",
            noHandoff);
        var localUser = RequiredIndex(source, "_platform.LocalUser", membership);
        var ready = RequiredIndex(source, "MessageKind.Ready", localUser);

        Assert.True(bootstrap < noHandoff);
        Assert.True(noHandoff < membership);
        Assert.True(membership < localUser);
        Assert.True(localUser < ready);
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
    public void ReceiveWorkIsBoundedPerDispatcherTickAndByFreePayloadSlots()
    {
        var source = ReadExchange();
        var drain = RequiredIndex(source, "private void DrainConnectionMessages(ConnectionContext context)");
        var nextMethod = RequiredIndex(source, "private void DispatchMessage(", drain);
        var body = source[drain..nextMethod];

        Assert.Contains("var maximumMessages = ReceiveBatchSize;", body, StringComparison.Ordinal);
        Assert.Contains("chunks.Reader.Count", body, StringComparison.Ordinal);
        Assert.Contains("Math.Min(maximumMessages, availablePayloadSlots)", body, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(body, "ReceiveMessagesOnConnection("));
        Assert.DoesNotContain("while (true)", body, StringComparison.Ordinal);
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

        Assert.Contains("private const int ProtocolVersion = 2;", source, StringComparison.Ordinal);
        Assert.Contains("actualTransferId != expectedTransferId", source, StringComparison.Ordinal);
        Assert.Contains("protocolVersion != ProtocolVersion", source, StringComparison.Ordinal);
        Assert.Contains("!Enum.IsDefined(envelope.Purpose)", source, StringComparison.Ordinal);
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
