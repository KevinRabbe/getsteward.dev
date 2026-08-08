using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamPeerGameDatagramBridgeCompositionTests
{
    [Fact]
    public void GameDataPlaneUsesSeparateSteamVirtualPortAndDoesNotOwnSteamLifetime()
    {
        var source = ReadBridge();

        Assert.Contains("private const int VirtualPort = 72;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("VirtualPort = 71", source, StringComparison.Ordinal);
        Assert.Contains("SteamNetworkingSockets.CreateListenSocketP2P(", source, StringComparison.Ordinal);
        Assert.Contains("SteamNetworkingSockets.ConnectP2P(", source, StringComparison.Ordinal);
        Assert.Contains("SteamNetworkingUtils.InitRelayNetworkAccess();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamAPI.Init", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamAPI.Shutdown", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PacketPumpRunsOffWpfDispatcher()
    {
        var source = ReadBridge();

        Assert.Contains("_receiveLoop = Task.Run(ReceiveLoopAsync);", source, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() =>\n                PumpHostUdpToSteamAsync", source, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() =>\n                PumpClientUdpToSteamAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DispatcherTimer", source, StringComparison.Ordinal);
    }

    [Fact]
    public void GameDatagramsAreUnreliableWhileControlIsReliable()
    {
        var source = ReadBridge();
        var datagram = RequiredIndex(source, "private static void SendUnreliableDatagram(");
        var unreliable = RequiredIndex(
            source,
            "Constants.k_nSteamNetworkingSend_UnreliableNoNagle",
            datagram);
        var reliable = RequiredIndex(source, "private async Task SendReliableControlAsync(", unreliable);
        var reliableFlag = RequiredIndex(
            source,
            "Constants.k_nSteamNetworkingSend_ReliableNoNagle",
            reliable);

        Assert.True(datagram < unreliable);
        Assert.True(unreliable < reliable);
        Assert.True(reliable < reliableFlag);
        Assert.Contains("EResult.k_EResultLimitExceeded", source, StringComparison.Ordinal);
        Assert.Contains("Preserve UDP semantics", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenIdentityComesFromAuthenticatedSteamConnectionNotPayload()
    {
        var source = ReadBridge();
        var authorize = RequiredIndex(source, "private async Task AuthorizeHostBridgeAsync(");
        var remote = RequiredIndex(
            source,
            "var remoteUser = ToSteamUser(context.RemoteSteamId);",
            authorize);
        var admission = RequiredIndex(
            source,
            "var grant = await _admission.AuthorizeAsync(",
            remote);

        var envelopeStart = RequiredIndex(source, "private sealed record OpenEnvelope(");
        var envelopeEnd = RequiredIndex(source, "private sealed record ReadyEnvelope(", envelopeStart);
        var envelope = source[envelopeStart..envelopeEnd];

        Assert.True(authorize < remote);
        Assert.True(remote < admission);
        Assert.DoesNotContain("UserIdentity", envelope, StringComparison.Ordinal);
        Assert.Contains("WorldId WorldId", envelope, StringComparison.Ordinal);
        Assert.Contains("ulong AuthorityGeneration", envelope, StringComparison.Ordinal);
    }

    [Fact]
    public void HostRealAddressAndPortNeverAppearInReadyEnvelope()
    {
        var source = ReadBridge();
        var readyStart = RequiredIndex(source, "private sealed record ReadyEnvelope(");
        var readyEnd = RequiredIndex(source, "private sealed record RejectEnvelope(", readyStart);
        var ready = source[readyStart..readyEnd];

        Assert.Contains("string? JoinToken", ready, StringComparison.Ordinal);
        Assert.DoesNotContain("HostUdpPort", ready, StringComparison.Ordinal);
        Assert.DoesNotContain("Address", ready, StringComparison.Ordinal);
        Assert.DoesNotContain("Port", ready, StringComparison.Ordinal);
    }

    [Fact]
    public void HostUdpSocketConnectsOnlyToAuthorizedLoopbackManagedPort()
    {
        var source = ReadBridge();
        var authorize = RequiredIndex(source, "private async Task AuthorizeHostBridgeAsync(");
        var grant = RequiredIndex(source, "var grant = await _admission.AuthorizeAsync(", authorize);
        var socket = RequiredIndex(source, "var socket = new Socket(", grant);
        var loopback = RequiredIndex(source, "IPAddress.Loopback,", socket);
        var port = RequiredIndex(source, "grant.HostUdpPort", loopback);
        var ready = RequiredIndex(source, "MessageKind.Ready", port);

        Assert.True(authorize < grant);
        Assert.True(grant < socket);
        Assert.True(socket < loopback);
        Assert.True(loopback < port);
        Assert.True(port < ready);
    }

    [Fact]
    public void JoiningGameReceivesLoopbackProxyOnly()
    {
        var source = ReadBridge();
        var open = RequiredIndex(source, "public async Task<SteamPeerGameDatagramClientSession> OpenClientAsync(");
        var bind = RequiredIndex(
            source,
            "localSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));",
            open);
        var loopbackConnection = RequiredIndex(
            source,
            "PeerGameDatagramBridgeAdmissionService.CreateLoopbackClientConnection(",
            bind);

        Assert.True(open < bind);
        Assert.True(bind < loopbackConnection);
        Assert.DoesNotContain("IPAddress.Any", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EachHostPeerGetsItsOwnUdpSocketAndConnectionContext()
    {
        var source = ReadBridge();

        Assert.Contains(
            "var context = new HostBridgeContext(\n            change.m_hConn,\n            remoteSteamId);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("private sealed class HostBridgeContext", source, StringComparison.Ordinal);
        Assert.Contains("public Socket? HostSocket", source, StringComparison.Ordinal);
        Assert.Contains("context.AttachHostSocket(socket);", source, StringComparison.Ordinal);
        Assert.Contains("MaximumIncomingConnections = 64", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DatagramAndControlSizesAreExplicitlyBounded()
    {
        var source = ReadBridge();

        Assert.Contains("MaximumControlBytes = 16 * 1024", source, StringComparison.Ordinal);
        Assert.Contains("MaximumUdpPayloadBytes = 65_507", source, StringComparison.Ordinal);
        Assert.Contains("native.m_cbSize > MaximumUdpPayloadBytes + 1", source, StringComparison.Ordinal);
        Assert.Contains("json.Length + 1 > MaximumControlBytes", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientOpenRequiresCurrentConfirmedLobbyBeforeSteamConnect()
    {
        var source = ReadBridge();
        var open = RequiredIndex(source, "public async Task<SteamPeerGameDatagramClientSession> OpenClientAsync(");
        var lobby = RequiredIndex(source, "var lobby = await _lobby.GetAsync(worldId", open);
        var confirmed = RequiredIndex(source, "!lobby.OwnerConfirmed", lobby);
        var generation = RequiredIndex(source, "lobby.AuthorityGeneration != authorityGeneration", confirmed);
        var handoff = RequiredIndex(source, "lobby.RequestedHost is not null", generation);
        var host = RequiredIndex(source, "!SameUser(lobby.Owner, confirmedHost)", handoff);
        var connect = RequiredIndex(source, "var context = await CreateOutgoingConnectionAsync(", host);

        Assert.True(lobby < confirmed);
        Assert.True(confirmed < generation);
        Assert.True(generation < handoff);
        Assert.True(handoff < host);
        Assert.True(host < connect);
    }

    private static string ReadBridge()
        => File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/SteamPeerGameDatagramBridge.cs"));

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
