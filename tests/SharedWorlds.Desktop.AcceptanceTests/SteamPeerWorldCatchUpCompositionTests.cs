using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamPeerWorldCatchUpCompositionTests
{
    [Fact]
    public void CatchUpUsesTheExistingPort71WorldExchange()
    {
        var source = Read("src/SharedWorlds.Desktop/SteamPeerWorldRevisionExchange.cs");

        Assert.Contains("IPeerWorldCatchUpRequestClient,", source, StringComparison.Ordinal);
        Assert.Contains("private const int VirtualPort = 71;", source, StringComparison.Ordinal);
        Assert.Contains("MessageKind.CatchUpRequest", source, StringComparison.Ordinal);
        Assert.Contains("MessageKind.CatchUpResult", source, StringComparison.Ordinal);
        Assert.DoesNotContain("VirtualPort = 73", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateListenSocketP2P(\n            73", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientProvesAttachedLobbyHostAndGenerationBeforeSendingRequest()
    {
        var source = Read("src/SharedWorlds.Desktop/SteamPeerWorldRevisionExchange.cs");
        var method = RequiredIndex(source, "public async Task<PeerWorldCatchUpResult> RequestCatchUpAsync(");
        var lobby = RequiredIndex(source, "var snapshot = await _lobby.GetAsync(request.WorldId, cancellationToken)", method);
        var authorityCheck = RequiredIndex(source, "snapshot.AuthorityGeneration != request.AuthorityGeneration", lobby);
        var ownerCheck = RequiredIndex(source, "!SameUser(snapshot.Owner, confirmedHost)", authorityCheck);
        var outgoingGate = RequiredIndex(source, "await _outgoingGate.WaitAsync(cancellationToken);", ownerCheck);
        var send = RequiredIndex(source, "MessageKind.CatchUpRequest", outgoingGate);

        Assert.True(method < lobby);
        Assert.True(lobby < authorityCheck);
        Assert.True(authorityCheck < ownerCheck);
        Assert.True(ownerCheck < outgoingGate);
        Assert.True(outgoingGate < send);
        Assert.Contains("snapshot.RequestedHost is not null", source[lobby..outgoingGate], StringComparison.Ordinal);
    }

    [Fact]
    public void CatchUpRequestContainsNoRemoteIdentityAndHostUsesAuthenticatedSteamConnection()
    {
        var source = Read("src/SharedWorlds.Desktop/SteamPeerWorldRevisionExchange.cs");
        var handler = RequiredIndex(source, "private async Task HandleCatchUpRequestAsync(");
        var remoteId = RequiredIndex(source, "context.RemoteSteamId.ToString(CultureInfo.InvariantCulture)", handler);
        var remoteUser = RequiredIndex(source, "var remoteUser = new UserIdentity(", remoteId);
        var router = RequiredIndex(source, "_catchUpRouter.HandleAsync(", remoteUser);
        var envelope = RequiredIndex(source, "private sealed record CatchUpRequestEnvelope(");
        var envelopeEnd = RequiredIndex(source, ");", envelope);
        var envelopeText = source[envelope..(envelopeEnd + 2)];

        Assert.True(handler < remoteId);
        Assert.True(remoteId < remoteUser);
        Assert.True(remoteUser < router);
        Assert.DoesNotContain("UserIdentity", envelopeText, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoteSteamId", envelopeText, StringComparison.Ordinal);
    }

    [Fact]
    public void CatchUpControlIsReliableBoundedAndAllocatesNoRevisionPayloadBuffer()
    {
        var source = Read("src/SharedWorlds.Desktop/SteamPeerWorldRevisionExchange.cs");
        var handler = RequiredIndex(source, "private async Task HandleCatchUpRequestAsync(");
        var nextMethod = RequiredIndex(source, "private async Task AuthorizeIncomingOfferAsync(", handler);
        var catchUpHandler = source[handler..nextMethod];
        var requestDispatch = RequiredIndex(
            source,
            "case MessageKind.CatchUpRequest when incoming.State == IncomingState.AwaitingOffer:");
        var payloadDispatch = RequiredIndex(
            source,
            "case MessageKind.Payload when incoming.State == IncomingState.Receiving:",
            requestDispatch);
        var requestSection = source[requestDispatch..payloadDispatch];

        Assert.Contains("SendReliableAsync(", catchUpHandler, StringComparison.Ordinal);
        Assert.Contains("MaxControlBytes", catchUpHandler, StringComparison.Ordinal);
        Assert.Contains("IncomingState.HandlingCatchUp", requestSection, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateIncomingBuffer", catchUpHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("Channel.CreateBounded", catchUpHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateIncomingBuffer", requestSection, StringComparison.Ordinal);
        Assert.DoesNotContain("Channel.CreateBounded", requestSection, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectAndConnectionFailureCompleteCatchUpResultExceptionally()
    {
        var source = Read("src/SharedWorlds.Desktop/SteamPeerWorldRevisionExchange.cs");

        Assert.Contains("outgoing.CatchUpResult.TrySetException(exception);", source, StringComparison.Ordinal);
        Assert.Contains("Outgoing?.CatchUpResult.TrySetException(exception);", source, StringComparison.Ordinal);
        Assert.Contains("TaskCompletionSource<PeerWorldCatchUpResult> CatchUpResult", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeBindsCatchUpRouterAfterSourceTransferServicesExist()
    {
        var source = Read("src/SharedWorlds.Desktop/StewardDesktopPeerRuntime.cs");
        var router = RequiredIndex(source, "var catchUpRouter = new PeerWorldCatchUpRequestRouter(");
        var exchange = RequiredIndex(source, "revisionExchange = new SteamPeerWorldRevisionExchange(", router);
        var catchUpArgument = RequiredIndex(source, "catchUpRouter,", exchange);
        var leaveArgument = RequiredIndex(source, "leaveRouter);", catchUpArgument);
        var bootstrap = RequiredIndex(source, "var bootstrap = new PeerWorldBootstrapTransferService(", leaveArgument);
        var observer = RequiredIndex(source, "var observerSync = new PeerWorldObserverSyncService(", bootstrap);
        var bind = RequiredIndex(source, "catchUpRouter.Bind(", observer);

        Assert.True(router < exchange);
        Assert.True(exchange < catchUpArgument);
        Assert.True(catchUpArgument < leaveArgument);
        Assert.True(leaveArgument < bootstrap);
        Assert.True(bootstrap < observer);
        Assert.True(observer < bind);
        Assert.Contains("public IPeerWorldCatchUpRequestClient CatchUp { get; }", source, StringComparison.Ordinal);
        Assert.Contains("CatchUp = catchUp;", source, StringComparison.Ordinal);
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
