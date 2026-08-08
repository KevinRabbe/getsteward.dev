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
    public void OneSocketEngineImplementsBootstrapHandoffAndObserverTransfers()
    {
        var source = ReadExchange();

        Assert.Contains("IPeerWorldRevisionExchange,", source, StringComparison.Ordinal);
        Assert.Contains("IPeerWorldBootstrapExchange,", source, StringComparison.Ordinal);
        Assert.Contains("IPeerWorldObserverSyncExchange,", source, StringComparison.Ordinal);
        Assert.Contains("TransferPurpose.HandoffRevision", source, StringComparison.Ordinal);
        Assert.Contains("TransferPurpose.Bootstrap", source, StringComparison.Ordinal);
        Assert.Contains("TransferPurpose.ObserverSync", source, StringComparison.Ordinal);
        Assert.Contains("TransferObserverRevisionAsync(", source, StringComparison.Ordinal);
        Assert.Contains("await _revisionInstaller.InstallAsync(", source, StringComparison.Ordinal);
        Assert.Contains("await _bootstrapInstaller.InstallAsync(", source, StringComparison.Ordinal);
        Assert.Contains("await _observerSyncInstaller.InstallAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void IncomingOfferMustComeFromConfirmedLiveHostAndCarryPersistentAuthority()
    {
        var source = ReadExchange();
        var authorize = RequiredIndex(source, "private async Task AuthorizeIncomingOfferAsync(");
        var snapshot = RequiredIndex(source, "var snapshot = await _lobby.GetAsync(offer.World.Id);", authorize);
        var confirmed = RequiredIndex(source, "!snapshot.OwnerConfirmed", snapshot);
        var liveGeneration = RequiredIndex(source, "snapshot.AuthorityGeneration == 0", confirmed);
        var remoteHost = RequiredIndex(source, "!SameSteamUser(snapshot.Owner, context.RemoteSteamId)", liveGeneration);
        var authority = RequiredIndex(source, "var authority = offer.World.PeerAuthority", remoteHost);
        var authorityGeneration = RequiredIndex(source, "authority.Generation == 0", authority);
        var authorityMember = RequiredIndex(source, "SameUser(member, authority.Holder)", authorityGeneration);

        Assert.True(snapshot < confirmed);
        Assert.True(confirmed < liveGeneration);
        Assert.True(liveGeneration < remoteHost);
        Assert.True(remoteHost < authority);
        Assert.True(authority < authorityGeneration);
        Assert.True(authorityGeneration < authorityMember);
    }

    [Fact]
    public void IncomingHandoffMustMatchRequestedLocalHolderAtExactlyNextGenerationBeforeReady()
    {
        var source = ReadExchange();
        var handoffPurpose = RequiredIndex(source, "case TransferPurpose.HandoffRevision:");
        var requested = RequiredIndex(source, "snapshot.RequestedHost is null", handoffPurpose);
        var localTarget = RequiredIndex(
            source,
            "snapshot.RequestedHost,\n                            _platform.LocalSteamId.m_SteamID",
            requested);
        var nextGeneration = RequiredIndex(
            source,
            "authority.Generation != snapshot.AuthorityGeneration + 1",
            localTarget);
        var authorityLocal = RequiredIndex(
            source,
            "SameSteamUser(authority.Holder, _platform.LocalSteamId.m_SteamID)",
            nextGeneration);
        var sameRequested = RequiredIndex(
            source,
            "SameUser(authority.Holder, snapshot.RequestedHost)",
            authorityLocal);
        var ready = RequiredIndex(source, "MessageKind.Ready", sameRequested);

        Assert.True(handoffPurpose < requested);
        Assert.True(requested < localTarget);
        Assert.True(localTarget < nextGeneration);
        Assert.True(nextGeneration < authorityLocal);
        Assert.True(authorityLocal < sameRequested);
        Assert.True(sameRequested < ready);
    }

    [Theory]
    [InlineData("Bootstrap")]
    [InlineData("ObserverSync")]
    public void BootstrapAndObserverRequireCurrentAuthorityNoHandoffAndCanonicalLocalMembership(
        string purpose)
    {
        var source = ReadExchange();
        var purposeCase = RequiredIndex(source, $"case TransferPurpose.{purpose}:");
        var authorityCheck = RequiredIndex(
            source,
            "EnsureCurrentAuthorityOffer(snapshot, authority",
            purposeCase);
        var admissionCheck = RequiredIndex(
            source,
            "EnsureObserverAdmission(snapshot, offer",
            authorityCheck);
        var currentAuthorityMethod = RequiredIndex(
            source,
            "private void EnsureCurrentAuthorityOffer(",
            admissionCheck);
        var exactGeneration = RequiredIndex(
            source,
            "authority.Generation != snapshot.AuthorityGeneration",
            currentAuthorityMethod);
        var exactHolder = RequiredIndex(
            source,
            "!SameUser(authority.Holder, snapshot.Owner)",
            exactGeneration);
        var admissionMethod = RequiredIndex(
            source,
            "private void EnsureObserverAdmission(",
            exactHolder);
        var noHandoff = RequiredIndex(source, "if (snapshot.RequestedHost is not null)", admissionMethod);
        var membership = RequiredIndex(
            source,
            "offer.World.Members.Any(member =>",
            noHandoff);
        var localIdentity = RequiredIndex(
            source,
            "SameSteamUser(member, _platform.LocalSteamId.m_SteamID)",
            membership);

        Assert.True(purposeCase < authorityCheck);
        Assert.True(authorityCheck < admissionCheck);
        Assert.True(currentAuthorityMethod < exactGeneration);
        Assert.True(exactGeneration < exactHolder);
        Assert.True(admissionMethod < noHandoff);
        Assert.True(noHandoff < membership);
        Assert.True(membership < localIdentity);
    }

    [Fact]
    public void ObserverInstallerIsRequiredBySocketEngineConstruction()
    {
        var source = ReadExchange();
        var constructor = RequiredIndex(source, "public SteamPeerWorldRevisionExchange(");
        var parameter = RequiredIndex(
            source,
            "PeerWorldObserverSyncInstaller observerSyncInstaller",
            constructor);
        var nullGuard = RequiredIndex(
            source,
            "ArgumentNullException.ThrowIfNull(observerSyncInstaller);",
            parameter);
        var assignment = RequiredIndex(
            source,
            "_observerSyncInstaller = observerSyncInstaller;",
            nullGuard);

        Assert.True(constructor < parameter);
        Assert.True(parameter < nullGuard);
        Assert.True(nullGuard < assignment);
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
    public void ControlMessagesAreBoundToOneProtocolTransferIdAndKnownPurpose()
    {
        var source = ReadExchange();

        Assert.Contains("private const int ProtocolVersion = 2;", source, StringComparison.Ordinal);
        Assert.Contains("actualTransferId != expectedTransferId", source, StringComparison.Ordinal);
        Assert.Contains("protocolVersion != ProtocolVersion", source, StringComparison.Ordinal);
        Assert.Contains("!Enum.IsDefined(envelope.Purpose)", source, StringComparison.Ordinal);
        Assert.Contains("ObserverSync = 3", source, StringComparison.Ordinal);
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
