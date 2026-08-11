using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Sessions;
using Steamworks;

namespace SharedWorlds.Desktop;

/// <summary>
/// Separate Steam P2P data plane for managed-game UDP traffic. Steward authority/state replication
/// remains on virtual port 71; this bridge uses virtual port 72 so game datagrams cannot head-of-line
/// block World control/save traffic.
///
/// Open/Ready/Reject/Close are reliable control messages. Game datagrams preserve UDP semantics and
/// are sent unreliably. The host never publishes its real address or game port to the joining peer.
/// </summary>
internal sealed class SteamPeerGameDatagramBridge : IDisposable, IAsyncDisposable
{
    private const int ProtocolVersion = 1;
    private const int VirtualPort = 72;
    private const int MaximumIncomingConnections = 64;
    private const int ReceiveBatchSize = 64;
    private const int MaximumControlBytes = 16 * 1024;
    private const int MaximumRejectTextBytes = 4096;
    private const int MaximumUdpPayloadBytes = 65_507;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ReliableBackpressureDelay = TimeSpan.FromMilliseconds(5);

    private readonly SteamPlatformRuntime _platform;
    private readonly SteamPeerWorldLobby _lobby;
    private readonly PeerGameDatagramBridgeAdmissionService _admission;
    private readonly PeerWorldLiveMemberRevocationRegistry? _liveRevocations;
    private readonly Callback<SteamNetConnectionStatusChangedCallback_t> _connectionCallback;
    private readonly ConcurrentDictionary<HSteamNetConnection, BridgeConnectionContext> _connections = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HSteamListenSocket _listenSocket;
    private readonly Task _receiveLoop;
    private int _disposed;

    public SteamPeerGameDatagramBridge(
        SteamPlatformRuntime platform,
        SteamPeerWorldLobby lobby,
        PeerGameDatagramBridgeAdmissionService admission,
        PeerWorldLiveMemberRevocationRegistry? liveRevocations = null)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(admission);
        if (!platform.Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "Steam peer game bridge must be created on Steward's Steam dispatcher.");
        }

        _platform = platform;
        _lobby = lobby;
        _admission = admission;
        _liveRevocations = liveRevocations;
        SteamNetworkingUtils.InitRelayNetworkAccess();
        _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(
            VirtualPort,
            0,
            []);
        if (_listenSocket == HSteamListenSocket.Invalid)
        {
            throw new InvalidOperationException(
                "Steam could not create Steward's peer game-data listen socket.");
        }

        _connectionCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(
            OnConnectionStatusChanged);
        _receiveLoop = Task.Run(ReceiveLoopAsync);
        if (_liveRevocations is not null)
        {
            _liveRevocations.Revoked += OnMemberRevoked;
        }
    }

    public async Task<SteamPeerGameDatagramClientSession> OpenClientAsync(
        WorldId worldId,
        ulong authorityGeneration,
        UserIdentity confirmedHost,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(confirmedHost);
        if (authorityGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(authorityGeneration),
                "Peer game bridge requires a nonzero authority generation.");
        }

        var hostSteamId = ParseSteamIdentity(confirmedHost);
        if (hostSteamId == _platform.LocalSteamId)
        {
            throw new InvalidOperationException(
                "The local Steward host does not need a peer game bridge to itself.");
        }

        var lobby = await _lobby.GetAsync(worldId, cancellationToken)
            ?? throw new InvalidOperationException(
                "The World has no active Steam peer lobby.");
        if (!lobby.OwnerConfirmed ||
            lobby.AuthorityGeneration != authorityGeneration ||
            lobby.RequestedHost is not null ||
            !SameUser(lobby.Owner, confirmedHost))
        {
            throw new InvalidOperationException(
                "The requested game bridge host/generation is not the currently confirmed live World host.");
        }

        var bridgeId = Guid.NewGuid();
        var context = await CreateOutgoingConnectionAsync(
            hostSteamId,
            bridgeId,
            cancellationToken);
        try
        {
            await WaitWithTimeoutAsync(
                context.Connected.Task,
                ConnectTimeout,
                "Steam did not establish the peer game bridge in time.",
                cancellationToken);

            await SendReliableControlAsync(
                context.Connection,
                EncodeControl(
                    MessageKind.Open,
                    new OpenEnvelope(
                        ProtocolVersion,
                        bridgeId,
                        worldId,
                        authorityGeneration),
                    "peer game bridge open"),
                cancellationToken);

            var ready = await WaitWithTimeoutAsync(
                context.Ready.Task,
                ReadyTimeout,
                "The peer host did not authorize the game bridge in time.",
                cancellationToken);
            EnsureControl(ready.ProtocolVersion, ready.BridgeId, bridgeId);

            var localSocket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp);
            localSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var localEndpoint = (IPEndPoint)(localSocket.LocalEndPoint
                ?? throw new InvalidOperationException(
                    "Steward could not observe its local peer game proxy endpoint."));
            context.AttachLocalProxy(localSocket);
            context.LocalPump = Task.Run(() =>
                PumpClientUdpToSteamAsync(context, _lifetime.Token));

            var gameConnection = PeerGameDatagramBridgeAdmissionService.CreateLoopbackClientConnection(
                localEndpoint.Port,
                ready.JoinToken);
            return new SteamPeerGameDatagramClientSession(
                gameConnection,
                () => CloseClientSessionAsync(context));
        }
        catch
        {
            await CloseConnectionAsync(
                context.Connection,
                "Steward peer game bridge open failed",
                linger: false);
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_liveRevocations is not null)
        {
            _liveRevocations.Revoked -= OnMemberRevoked;
        }

        _lifetime.Cancel();
        _connectionCallback.Dispose();
        if (_listenSocket != HSteamListenSocket.Invalid)
        {
            SteamNetworkingSockets.CloseListenSocket(_listenSocket);
        }

        foreach (var context in _connections.Values.ToArray())
        {
            context.Fail(new ObjectDisposedException(nameof(SteamPeerGameDatagramBridge)));
            SteamNetworkingSockets.CloseConnection(
                context.Connection,
                0,
                "Steward peer game bridge shutting down",
                false);
            _ = context.DisposeAsync();
        }

        _connections.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        try
        {
            await _receiveLoop;
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Dispose();
    }

    private async Task<ClientBridgeContext> CreateOutgoingConnectionAsync(
        CSteamID hostSteamId,
        Guid bridgeId,
        CancellationToken cancellationToken)
    {
        return await InvokeSteamAsync(
            () =>
            {
                var identity = new SteamNetworkingIdentity();
                identity.Clear();
                identity.SetSteamID(hostSteamId);
                var connection = SteamNetworkingSockets.ConnectP2P(
                    ref identity,
                    VirtualPort,
                    0,
                    []);
                if (connection == HSteamNetConnection.Invalid)
                {
                    throw new IOException(
                        "Steam rejected the peer game bridge connection request.");
                }

                var context = new ClientBridgeContext(
                    connection,
                    hostSteamId.m_SteamID,
                    bridgeId);
                if (!_connections.TryAdd(connection, context))
                {
                    SteamNetworkingSockets.CloseConnection(
                        connection,
                        0,
                        "Duplicate Steward bridge connection",
                        false);
                    throw new InvalidOperationException(
                        "Steward could not track the peer game bridge connection.");
                }

                return context;
            },
            cancellationToken);
    }

    private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t change)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var connection = change.m_hConn;
        var state = change.m_info.m_eState;
        if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting &&
            change.m_eOldState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None &&
            change.m_info.m_hListenSocket == _listenSocket)
        {
            AcceptIncomingConnection(change);
            return;
        }

        if (!_connections.TryGetValue(connection, out var context))
        {
            return;
        }

        if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected)
        {
            if (context is ClientBridgeContext client)
            {
                client.Connected.TrySetResult(true);
            }

            return;
        }

        if (state is
            ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer or
            ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
        {
            FailAndRemove(
                context,
                new IOException(
                    $"Steam peer game bridge connection ended: {change.m_info.m_szEndDebug}"));
        }
    }

    private void OnMemberRevoked(PeerWorldLiveMemberRevocation revocation)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            !string.Equals(revocation.Member.Provider, "steam", StringComparison.OrdinalIgnoreCase) ||
            !ulong.TryParse(
                revocation.Member.ExternalId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var revokedSteamId) ||
            revokedSteamId == 0)
        {
            return;
        }

        foreach (var context in _connections.Values
                     .OfType<HostBridgeContext>()
                     .ToArray())
        {
            if (context.RemoteSteamId != revokedSteamId ||
                context.WorldId is not { } contextWorldId ||
                contextWorldId != revocation.WorldId ||
                context.AuthorityGeneration != revocation.AuthorityGeneration)
            {
                continue;
            }

            FailAndRemove(
                context,
                new UnauthorizedAccessException(
                    $"Peer member '{revocation.Member.ExternalId}' was removed from live World '{revocation.WorldId}'."));
        }
    }

    private void AcceptIncomingConnection(SteamNetConnectionStatusChangedCallback_t change)
    {
        var remoteSteamId = change.m_info.m_identityRemote.GetSteamID64();
        if (remoteSteamId == 0 ||
            remoteSteamId == _platform.LocalSteamId.m_SteamID ||
            _connections.Values.Count(static context => context is HostBridgeContext) >= MaximumIncomingConnections)
        {
            SteamNetworkingSockets.CloseConnection(
                change.m_hConn,
                0,
                "Steward peer game bridge admission rejected",
                false);
            return;
        }

        var accepted = SteamNetworkingSockets.AcceptConnection(change.m_hConn);
        if (accepted != EResult.k_EResultOK)
        {
            SteamNetworkingSockets.CloseConnection(
                change.m_hConn,
                0,
                "Steward could not accept peer game bridge",
                false);
            return;
        }

        var context = new HostBridgeContext(
            change.m_hConn,
            remoteSteamId);
        if (!_connections.TryAdd(change.m_hConn, context))
        {
            SteamNetworkingSockets.CloseConnection(
                change.m_hConn,
                0,
                "Duplicate Steward bridge connection",
                false);
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var cancellationToken = _lifetime.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            var processedAny = false;
            foreach (var context in _connections.Values.ToArray())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    processedAny |= DrainConnection(context);
                }
                catch (Exception exception)
                {
                    FailAndRemove(context, exception);
                }
            }

            if (!processedAny)
            {
                await Task.Delay(1, cancellationToken);
            }
        }
    }

    private bool DrainConnection(BridgeConnectionContext context)
    {
        var pointers = new IntPtr[ReceiveBatchSize];
        var count = SteamNetworkingSockets.ReceiveMessagesOnConnection(
            context.Connection,
            pointers,
            pointers.Length);
        if (count < 0)
        {
            throw new IOException(
                "Steam reported an invalid peer game bridge connection while receiving.");
        }

        for (var index = 0; index < count; index++)
        {
            var pointer = pointers[index];
            try
            {
                var native = SteamNetworkingMessage_t.FromIntPtr(pointer);
                if (native.m_cbSize <= 0 ||
                    native.m_cbSize > MaximumUdpPayloadBytes + 1)
                {
                    throw new InvalidDataException(
                        "Steam peer game bridge message has an invalid size.");
                }

                var bytes = new byte[native.m_cbSize];
                Marshal.Copy(native.m_pData, bytes, 0, bytes.Length);
                DispatchMessage(context, bytes);
            }
            finally
            {
                SteamNetworkingMessage_t.Release(pointer);
            }
        }

        return count > 0;
    }

    private void DispatchMessage(BridgeConnectionContext context, byte[] message)
    {
        if (message.Length == 0)
        {
            throw new InvalidDataException("Steam peer game bridge message was empty.");
        }

        var kind = (MessageKind)message[0];
        switch (context)
        {
            case HostBridgeContext host:
                DispatchHostMessage(host, kind, message);
                break;
            case ClientBridgeContext client:
                DispatchClientMessage(client, kind, message);
                break;
            default:
                throw new InvalidOperationException(
                    "Steward peer game bridge has an unknown connection context.");
        }
    }

    private void DispatchHostMessage(
        HostBridgeContext context,
        MessageKind kind,
        byte[] message)
    {
        switch (kind)
        {
            case MessageKind.Open when context.State == HostBridgeState.AwaitingOpen:
                var open = DecodeControl<OpenEnvelope>(
                    message,
                    "peer game bridge open");
                if (open.ProtocolVersion != ProtocolVersion ||
                    open.BridgeId == Guid.Empty ||
                    open.AuthorityGeneration == 0)
                {
                    throw new InvalidDataException(
                        "Peer game bridge open uses an unsupported protocol or authority generation.");
                }

                // Bind the authenticated connection to its exact World/generation before asynchronous
                // authorization starts. A concurrent live revocation can therefore target this context
                // even if it has not reached Ready yet; admission also re-checks the deny fence.
                context.BridgeId = open.BridgeId;
                context.WorldId = open.WorldId;
                context.AuthorityGeneration = open.AuthorityGeneration;
                context.State = HostBridgeState.Authorizing;
                _ = Task.Run(() => AuthorizeHostBridgeAsync(context, open));
                break;

            case MessageKind.Datagram when context.State == HostBridgeState.Ready:
                ForwardSteamDatagramToHost(context, message);
                break;

            case MessageKind.Close:
                _ = CloseConnectionAsync(
                    context.Connection,
                    "Peer closed Steward game bridge",
                    linger: false);
                break;

            default:
                throw new InvalidDataException(
                    $"Unexpected peer game bridge message '{kind}' while host bridge is '{context.State}'.");
        }
    }

    private void DispatchClientMessage(
        ClientBridgeContext context,
        MessageKind kind,
        byte[] message)
    {
        switch (kind)
        {
            case MessageKind.Ready:
                var ready = DecodeControl<ReadyEnvelope>(
                    message,
                    "peer game bridge ready");
                EnsureControl(ready.ProtocolVersion, ready.BridgeId, context.BridgeId);
                context.Ready.TrySetResult(ready);
                break;

            case MessageKind.Reject:
                var rejection = DecodeControl<RejectEnvelope>(
                    message,
                    "peer game bridge rejection");
                EnsureControl(rejection.ProtocolVersion, rejection.BridgeId, context.BridgeId);
                context.Ready.TrySetException(new InvalidOperationException(
                    string.IsNullOrWhiteSpace(rejection.Reason)
                        ? "The peer host rejected the game bridge."
                        : rejection.Reason));
                break;

            case MessageKind.Datagram:
                ForwardSteamDatagramToLocalClient(context, message);
                break;

            case MessageKind.Close:
                FailAndRemove(
                    context,
                    new IOException("The peer host closed the game bridge."));
                break;

            default:
                throw new InvalidDataException(
                    $"Unexpected peer game bridge response '{kind}'.");
        }
    }

    private async Task AuthorizeHostBridgeAsync(
        HostBridgeContext context,
        OpenEnvelope open)
    {
        try
        {
            var remoteUser = ToSteamUser(context.RemoteSteamId);
            var grant = await _admission.AuthorizeAsync(
                open.WorldId,
                open.AuthorityGeneration,
                remoteUser,
                context.Cancellation.Token);

            if (!_connections.TryGetValue(context.Connection, out var current) ||
                !ReferenceEquals(current, context) ||
                context.State != HostBridgeState.Authorizing ||
                context.WorldId != open.WorldId ||
                context.AuthorityGeneration != open.AuthorityGeneration)
            {
                return;
            }

            var socket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp);
            socket.Connect(new IPEndPoint(
                IPAddress.Loopback,
                grant.HostUdpPort));
            if (!context.TryAttachHostSocket(socket))
            {
                socket.Dispose();
                return;
            }

            context.State = HostBridgeState.Ready;
            context.HostPump = Task.Run(() =>
                PumpHostUdpToSteamAsync(context, _lifetime.Token));

            await SendReliableControlAsync(
                context.Connection,
                EncodeControl(
                    MessageKind.Ready,
                    new ReadyEnvelope(
                        ProtocolVersion,
                        open.BridgeId,
                        grant.JoinToken),
                    "peer game bridge ready"),
                context.Cancellation.Token);
        }
        catch (Exception exception)
        {
            await RejectAndCloseHostAsync(context, exception.Message);
        }
    }

    private async Task PumpHostUdpToSteamAsync(
        HostBridgeContext context,
        CancellationToken lifetimeToken)
    {
        var socket = context.HostSocket
            ?? throw new InvalidOperationException(
                "Peer game bridge host socket was not attached.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeToken,
            context.Cancellation.Token);
        var buffer = new byte[MaximumUdpPayloadBytes];
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var count = await socket.ReceiveAsync(
                    buffer,
                    SocketFlags.None,
                    linked.Token);
                if (count <= 0)
                {
                    continue;
                }

                SendUnreliableDatagram(
                    context.Connection,
                    buffer.AsSpan(0, count));
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailAndRemove(context, exception);
        }
    }

    private async Task PumpClientUdpToSteamAsync(
        ClientBridgeContext context,
        CancellationToken lifetimeToken)
    {
        var socket = context.LocalSocket
            ?? throw new InvalidOperationException(
                "Peer game bridge local proxy socket was not attached.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            lifetimeToken,
            context.Cancellation.Token);
        var buffer = new byte[MaximumUdpPayloadBytes];
        EndPoint anyLoopback = new IPEndPoint(IPAddress.Loopback, 0);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var received = await socket.ReceiveFromAsync(
                    buffer,
                    SocketFlags.None,
                    anyLoopback,
                    linked.Token);
                if (received.ReceivedBytes <= 0 ||
                    received.RemoteEndPoint is not IPEndPoint remote ||
                    !IPAddress.IsLoopback(remote.Address))
                {
                    continue;
                }

                if (!context.TryBindLocalGameEndpoint(remote))
                {
                    continue;
                }

                SendUnreliableDatagram(
                    context.Connection,
                    buffer.AsSpan(0, received.ReceivedBytes));
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            FailAndRemove(context, exception);
        }
    }

    private static void ForwardSteamDatagramToHost(
        HostBridgeContext context,
        byte[] message)
    {
        if (message.Length <= 1 || message.Length > MaximumUdpPayloadBytes + 1)
        {
            throw new InvalidDataException(
                "Peer game bridge UDP datagram has an invalid length.");
        }

        var socket = context.HostSocket
            ?? throw new InvalidOperationException(
                "Peer game bridge host UDP socket is unavailable.");
        _ = socket.Send(
            message,
            1,
            message.Length - 1,
            SocketFlags.None);
    }

    private static void ForwardSteamDatagramToLocalClient(
        ClientBridgeContext context,
        byte[] message)
    {
        if (message.Length <= 1 || message.Length > MaximumUdpPayloadBytes + 1)
        {
            throw new InvalidDataException(
                "Peer game bridge UDP datagram has an invalid length.");
        }

        var socket = context.LocalSocket;
        var endpoint = context.GetLocalGameEndpoint();
        if (socket is null || endpoint is null)
        {
            // UDP has no connection handshake. The local game has not emitted its first datagram yet,
            // so Steward does not know which loopback source endpoint should receive server traffic.
            return;
        }

        _ = socket.SendTo(
            message,
            1,
            message.Length - 1,
            SocketFlags.None,
            endpoint);
    }

    private static void SendUnreliableDatagram(
        HSteamNetConnection connection,
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length <= 0 || payload.Length > MaximumUdpPayloadBytes)
        {
            return;
        }

        var message = new byte[payload.Length + 1];
        message[0] = (byte)MessageKind.Datagram;
        payload.CopyTo(message.AsSpan(1));
        var result = SendPinned(
            connection,
            message,
            Constants.k_nSteamNetworkingSend_UnreliableNoNagle);
        if (result is
            EResult.k_EResultOK or
            EResult.k_EResultLimitExceeded or
            EResult.k_EResultNoConnection or
            EResult.k_EResultInvalidState)
        {
            // Preserve UDP semantics: congestion/closure may drop a datagram rather than applying
            // unbounded backpressure to the game or the separate Steward control/state channel.
            return;
        }

        throw new IOException(
            $"Steam rejected a peer game UDP datagram: {result}.");
    }

    private async Task SendReliableControlAsync(
        HSteamNetConnection connection,
        byte[] message,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = SendPinned(
                connection,
                message,
                Constants.k_nSteamNetworkingSend_ReliableNoNagle);
            if (result == EResult.k_EResultOK)
            {
                return;
            }

            if (result == EResult.k_EResultLimitExceeded)
            {
                await Task.Delay(ReliableBackpressureDelay, cancellationToken);
                continue;
            }

            throw new IOException(
                $"Steam rejected a reliable peer game bridge control message: {result}.");
        }
    }

    private static EResult SendPinned(
        HSteamNetConnection connection,
        byte[] message,
        int sendFlags)
    {
        var pinned = GCHandle.Alloc(message, GCHandleType.Pinned);
        try
        {
            return SteamNetworkingSockets.SendMessageToConnection(
                connection,
                pinned.AddrOfPinnedObject(),
                checked((uint)message.Length),
                sendFlags,
                out _);
        }
        finally
        {
            pinned.Free();
        }
    }

    private async Task RejectAndCloseHostAsync(
        HostBridgeContext context,
        string reason)
    {
        if (context.State == HostBridgeState.Closed)
        {
            return;
        }

        context.State = HostBridgeState.Closed;
        var safeReason = BoundUtf8(reason, MaximumRejectTextBytes);
        try
        {
            if (context.BridgeId != Guid.Empty)
            {
                await SendReliableControlAsync(
                    context.Connection,
                    EncodeControl(
                        MessageKind.Reject,
                        new RejectEnvelope(
                            ProtocolVersion,
                            context.BridgeId,
                            safeReason),
                        "peer game bridge rejection"),
                    CancellationToken.None);
                _ = SteamNetworkingSockets.FlushMessagesOnConnection(context.Connection);
            }
        }
        catch
        {
        }

        await CloseConnectionAsync(
            context.Connection,
            safeReason,
            linger: true);
    }

    private async ValueTask CloseClientSessionAsync(ClientBridgeContext context)
    {
        if (!_connections.ContainsKey(context.Connection))
        {
            return;
        }

        try
        {
            await SendReliableControlAsync(
                context.Connection,
                EncodeControl(
                    MessageKind.Close,
                    new CloseEnvelope(
                        ProtocolVersion,
                        context.BridgeId),
                    "peer game bridge close"),
                CancellationToken.None);
        }
        catch
        {
        }

        await CloseConnectionAsync(
            context.Connection,
            "Local game client closed Steward peer bridge",
            linger: false);
    }

    private async Task CloseConnectionAsync(
        HSteamNetConnection connection,
        string reason,
        bool linger)
    {
        if (!_connections.TryRemove(connection, out var context))
        {
            return;
        }

        context.Cancellation.Cancel();
        SteamNetworkingSockets.CloseConnection(
            connection,
            0,
            BoundUtf8(
                reason,
                Constants.k_cchSteamNetworkingMaxConnectionCloseReason - 1),
            linger);
        await context.DisposeAsync();
    }

    private void FailAndRemove(
        BridgeConnectionContext context,
        Exception exception)
    {
        if (!_connections.TryRemove(context.Connection, out _))
        {
            return;
        }

        context.Fail(exception);
        SteamNetworkingSockets.CloseConnection(
            context.Connection,
            0,
            "Steward peer game bridge failed",
            false);
        _ = context.DisposeAsync();
    }

    private static byte[] EncodeControl<T>(
        MessageKind kind,
        T value,
        string description)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value);
        if (json.Length + 1 > MaximumControlBytes)
        {
            throw new InvalidDataException(
                $"Serialized {description} exceeds Steward's {MaximumControlBytes}-byte bound.");
        }

        var message = new byte[json.Length + 1];
        message[0] = (byte)kind;
        Buffer.BlockCopy(json, 0, message, 1, json.Length);
        return message;
    }

    private static T DecodeControl<T>(
        byte[] message,
        string description)
    {
        if (message.Length <= 1 || message.Length > MaximumControlBytes)
        {
            throw new InvalidDataException(
                $"Serialized {description} is outside Steward's accepted size bound.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(message.AsSpan(1))
                ?? throw new InvalidDataException(
                    $"Serialized {description} was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Serialized {description} is malformed.",
                exception);
        }
    }

    private static void EnsureControl(
        int protocolVersion,
        Guid actualBridgeId,
        Guid expectedBridgeId)
    {
        if (protocolVersion != ProtocolVersion ||
            actualBridgeId != expectedBridgeId)
        {
            throw new InvalidDataException(
                "Peer game bridge control message does not match the active session.");
        }
    }

    private static CSteamID ParseSteamIdentity(UserIdentity user)
    {
        if (!string.Equals(user.Provider, "steam", StringComparison.OrdinalIgnoreCase) ||
            !ulong.TryParse(
                user.ExternalId,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var steamId) ||
            steamId == 0)
        {
            throw new ArgumentException(
                "A valid Steam UserIdentity is required for the peer game bridge.",
                nameof(user));
        }

        return new CSteamID(steamId);
    }

    private static UserIdentity ToSteamUser(ulong steamId)
    {
        if (steamId == 0)
        {
            throw new InvalidDataException(
                "Steam peer game bridge has an invalid remote Steam identity.");
        }

        var externalId = steamId.ToString(CultureInfo.InvariantCulture);
        return new UserIdentity("steam", externalId, externalId);
    }

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);

    private static string BoundUtf8(string? text, int maximumBytes)
    {
        var value = string.IsNullOrWhiteSpace(text)
            ? "Steward peer game bridge failed."
            : text.Trim();
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            return value;
        }

        var builder = new StringBuilder();
        var usedBytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeText = rune.ToString();
            var runeBytes = Encoding.UTF8.GetByteCount(runeText);
            if (usedBytes + runeBytes > maximumBytes)
            {
                break;
            }

            builder.Append(runeText);
            usedBytes += runeBytes;
        }

        return builder.Length == 0
            ? "Steward peer game bridge failed."
            : builder.ToString();
    }

    private async Task<T> InvokeSteamAsync<T>(
        Func<T> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (_platform.Dispatcher.CheckAccess())
        {
            return action();
        }

        return await _platform.Dispatcher.InvokeAsync(
            action,
            System.Windows.Threading.DispatcherPriority.Normal,
            cancellationToken).Task;
    }

    private static async Task<T> WaitWithTimeoutAsync<T>(
        Task<T> task,
        TimeSpan timeout,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await task.WaitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(timeoutMessage);
        }
    }

    private enum MessageKind : byte
    {
        Open = 1,
        Ready = 2,
        Datagram = 3,
        Close = 4,
        Reject = 5
    }

    private enum HostBridgeState
    {
        AwaitingOpen = 0,
        Authorizing = 1,
        Ready = 2,
        Closed = 3
    }

    private sealed record OpenEnvelope(
        int ProtocolVersion,
        Guid BridgeId,
        WorldId WorldId,
        ulong AuthorityGeneration);

    private sealed record ReadyEnvelope(
        int ProtocolVersion,
        Guid BridgeId,
        string? JoinToken);

    private sealed record RejectEnvelope(
        int ProtocolVersion,
        Guid BridgeId,
        string Reason);

    private sealed record CloseEnvelope(
        int ProtocolVersion,
        Guid BridgeId);

    private abstract class BridgeConnectionContext : IAsyncDisposable
    {
        protected BridgeConnectionContext(
            HSteamNetConnection connection,
            ulong remoteSteamId)
        {
            Connection = connection;
            RemoteSteamId = remoteSteamId;
        }

        public HSteamNetConnection Connection { get; }
        public ulong RemoteSteamId { get; }
        public CancellationTokenSource Cancellation { get; } = new();

        public abstract void Fail(Exception exception);
        public abstract ValueTask DisposeAsync();
    }

    private sealed class HostBridgeContext : BridgeConnectionContext
    {
        private readonly object _resourceGate = new();

        public HostBridgeContext(
            HSteamNetConnection connection,
            ulong remoteSteamId)
            : base(connection, remoteSteamId)
        {
        }

        public Guid BridgeId { get; set; }
        public WorldId? WorldId { get; set; }
        public ulong AuthorityGeneration { get; set; }
        public HostBridgeState State { get; set; } = HostBridgeState.AwaitingOpen;
        public Socket? HostSocket { get; private set; }
        public Task? HostPump { get; set; }

        public bool TryAttachHostSocket(Socket socket)
        {
            ArgumentNullException.ThrowIfNull(socket);
            lock (_resourceGate)
            {
                if (Cancellation.IsCancellationRequested || HostSocket is not null)
                {
                    return false;
                }

                HostSocket = socket;
                return true;
            }
        }

        public override void Fail(Exception exception)
        {
            lock (_resourceGate)
            {
                Cancellation.Cancel();
            }
        }

        public override async ValueTask DisposeAsync()
        {
            Socket? hostSocket;
            lock (_resourceGate)
            {
                Cancellation.Cancel();
                hostSocket = HostSocket;
                HostSocket = null;
            }

            hostSocket?.Dispose();
            if (HostPump is not null &&
                Task.CurrentId != HostPump.Id)
            {
                try
                {
                    await HostPump;
                }
                catch
                {
                }
            }

            Cancellation.Dispose();
        }
    }

    private sealed class ClientBridgeContext : BridgeConnectionContext
    {
        private readonly object _endpointGate = new();
        private IPEndPoint? _localGameEndpoint;

        public ClientBridgeContext(
            HSteamNetConnection connection,
            ulong remoteSteamId,
            Guid bridgeId)
            : base(connection, remoteSteamId)
        {
            BridgeId = bridgeId;
        }

        public Guid BridgeId { get; }
        public TaskCompletionSource<bool> Connected { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ReadyEnvelope> Ready { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public Socket? LocalSocket { get; private set; }
        public Task? LocalPump { get; set; }

        public void AttachLocalProxy(Socket socket)
            => LocalSocket = socket;

        public bool TryBindLocalGameEndpoint(IPEndPoint endpoint)
        {
            lock (_endpointGate)
            {
                if (_localGameEndpoint is null)
                {
                    _localGameEndpoint = endpoint;
                    return true;
                }

                return _localGameEndpoint.Equals(endpoint);
            }
        }

        public IPEndPoint? GetLocalGameEndpoint()
        {
            lock (_endpointGate)
            {
                return _localGameEndpoint;
            }
        }

        public override void Fail(Exception exception)
        {
            Connected.TrySetException(exception);
            Ready.TrySetException(exception);
            Cancellation.Cancel();
        }

        public override async ValueTask DisposeAsync()
        {
            Cancellation.Cancel();
            LocalSocket?.Dispose();
            if (LocalPump is not null &&
                Task.CurrentId != LocalPump.Id)
            {
                try
                {
                    await LocalPump;
                }
                catch
                {
                }
            }

            Cancellation.Dispose();
        }
    }
}

internal sealed class SteamPeerGameDatagramClientSession : IAsyncDisposable
{
    private readonly Func<ValueTask> _close;
    private int _disposed;

    public SteamPeerGameDatagramClientSession(
        HostConnection gameConnection,
        Func<ValueTask> close)
    {
        ArgumentNullException.ThrowIfNull(gameConnection);
        ArgumentNullException.ThrowIfNull(close);
        GameConnection = gameConnection;
        _close = close;
    }

    public HostConnection GameConnection { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _close();
    }
}
