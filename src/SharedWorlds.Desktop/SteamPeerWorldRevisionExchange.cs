using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Sessions;
using Steamworks;

namespace SharedWorlds.Desktop;

/// <summary>
/// Identity-preserving World transfer over Steam Networking Sockets. One authenticated P2P engine
/// carries first-time Bootstrap, strict HandoffRevision, non-authoritative ObserverSync, and bounded
/// catch-up request/result control transactions. The purpose is part of the transfer protocol and
/// selects the corresponding receiver installer; transport never decides host authority. Steam
/// authenticates the peer identity and Steward additionally authorizes every operation against the
/// already-attached active World lobby and its exact generation.
/// </summary>
internal sealed class SteamPeerWorldRevisionExchange :
    IPeerWorldRevisionExchange,
    IPeerWorldBootstrapExchange,
    IPeerWorldObserverSyncExchange,
    IPeerWorldCatchUpRequestClient,
    IDisposable
{
    private const int ProtocolVersion = 2;
    private const int VirtualPort = 71;
    private const int PayloadChunkBytes = 64 * 1024;
    private const int ReceiveBatchSize = 16;
    private const int MaxIncomingConnections = 4;
    private const int MaxOfferBytes = 256 * 1024;
    private const int MaxControlBytes = 64 * 1024;
    private const int MaxRejectTextBytes = 4096;
    private const int MaxQueuedPayloadChunks = 64;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReceiptTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SendBackpressureDelay = TimeSpan.FromMilliseconds(10);

    private readonly SteamPlatformRuntime _platform;
    private readonly SteamPeerWorldLobby _lobby;
    private readonly PeerWorldRevisionReplicaInstaller _revisionInstaller;
    private readonly PeerWorldBootstrapInstaller _bootstrapInstaller;
    private readonly PeerWorldObserverSyncInstaller _observerSyncInstaller;
    private readonly PeerWorldCatchUpRequestRouter _catchUpRouter;
    private readonly Callback<SteamNetConnectionStatusChangedCallback_t> _connectionCallback;
    private readonly DispatcherTimer _receiveTimer;
    private readonly SemaphoreSlim _outgoingGate = new(1, 1);
    private readonly Dictionary<HSteamNetConnection, ConnectionContext> _connections = [];
    private readonly HSteamListenSocket _listenSocket;
    private bool _disposed;

    public SteamPeerWorldRevisionExchange(
        SteamPlatformRuntime platform,
        SteamPeerWorldLobby lobby,
        PeerWorldRevisionReplicaInstaller revisionInstaller,
        PeerWorldBootstrapInstaller bootstrapInstaller,
        PeerWorldObserverSyncInstaller observerSyncInstaller,
        PeerWorldCatchUpRequestRouter catchUpRouter)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(revisionInstaller);
        ArgumentNullException.ThrowIfNull(bootstrapInstaller);
        ArgumentNullException.ThrowIfNull(observerSyncInstaller);
        ArgumentNullException.ThrowIfNull(catchUpRouter);
        if (!platform.Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "Steam peer World exchange must be created on Steward's Steam dispatcher.");
        }

        _platform = platform;
        _lobby = lobby;
        _revisionInstaller = revisionInstaller;
        _bootstrapInstaller = bootstrapInstaller;
        _observerSyncInstaller = observerSyncInstaller;
        _catchUpRouter = catchUpRouter;
        SteamNetworkingUtils.InitRelayNetworkAccess();
        _listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(
            VirtualPort,
            0,
            []);
        if (_listenSocket == HSteamListenSocket.Invalid)
        {
            throw new InvalidOperationException(
                "Steam could not create Steward's peer World listen socket.");
        }

        _connectionCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(
            OnConnectionStatusChanged);
        _receiveTimer = new DispatcherTimer(DispatcherPriority.Background, platform.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(15)
        };
        _receiveTimer.Tick += OnReceiveTick;
        _receiveTimer.Start();
    }

    public Task<PeerWorldRevisionReceipt> TransferAsync(
        UserIdentity targetHost,
        PeerWorldRevisionOffer offer,
        Stream statePayload,
        CancellationToken cancellationToken = default)
        => TransferCoreAsync(
            targetHost,
            offer,
            statePayload,
            TransferPurpose.HandoffRevision,
            cancellationToken);

    public Task<PeerWorldRevisionReceipt> TransferBootstrapAsync(
        UserIdentity targetMember,
        PeerWorldRevisionOffer offer,
        Stream statePayload,
        CancellationToken cancellationToken = default)
        => TransferCoreAsync(
            targetMember,
            offer,
            statePayload,
            TransferPurpose.Bootstrap,
            cancellationToken);

    public Task<PeerWorldRevisionReceipt> TransferObserverRevisionAsync(
        UserIdentity targetMember,
        PeerWorldRevisionOffer offer,
        Stream statePayload,
        CancellationToken cancellationToken = default)
        => TransferCoreAsync(
            targetMember,
            offer,
            statePayload,
            TransferPurpose.ObserverSync,
            cancellationToken);

    public async Task<PeerWorldCatchUpResult> RequestCatchUpAsync(
        UserIdentity confirmedHost,
        PeerWorldCatchUpRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmedHost);
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request.WorldId.Value == Guid.Empty || request.AuthorityGeneration == 0)
        {
            throw new InvalidDataException(
                "Peer World catch-up request requires a World ID and nonzero authority generation.");
        }

        if (request.LocalStateRevisionId is { } localRevision &&
            localRevision.Value == Guid.Empty)
        {
            throw new InvalidDataException(
                "Peer World catch-up request contains an empty local state revision ID.");
        }

        var hostSteamId = ParseSteamIdentity(confirmedHost);
        if (hostSteamId == _platform.LocalSteamId)
        {
            throw new InvalidOperationException(
                "The local Steward host cannot request peer catch-up from itself.");
        }

        var snapshot = await _lobby.GetAsync(request.WorldId, cancellationToken)
            ?? throw new InvalidOperationException(
                "Steward is not attached to the active Steam lobby for this World.");
        if (!snapshot.OwnerConfirmed ||
            snapshot.AuthorityGeneration != request.AuthorityGeneration ||
            snapshot.RequestedHost is not null ||
            !SameUser(snapshot.Owner, confirmedHost))
        {
            throw new InvalidOperationException(
                "Peer catch-up request does not match the currently confirmed live host/generation or a handoff is in progress.");
        }

        await _outgoingGate.WaitAsync(cancellationToken);
        HSteamNetConnection connection = HSteamNetConnection.Invalid;
        try
        {
            var requestId = Guid.NewGuid();
            var created = await CreateOutgoingConnectionAsync(
                hostSteamId,
                requestId,
                cancellationToken);
            connection = created.Connection;
            var outgoing = created.Context;

            await WaitWithTimeoutAsync(
                outgoing.Connected.Task,
                ConnectTimeout,
                "Steam did not establish the peer catch-up control connection in time.",
                cancellationToken);

            await SendReliableAsync(
                connection,
                EncodeJson(
                    MessageKind.CatchUpRequest,
                    new CatchUpRequestEnvelope(
                        ProtocolVersion,
                        requestId,
                        request),
                    MaxControlBytes,
                    "peer World catch-up request"),
                cancellationToken);
            await InvokeSteamAsync(
                () => SteamNetworkingSockets.FlushMessagesOnConnection(connection),
                cancellationToken);

            var result = await WaitWithTimeoutAsync(
                outgoing.CatchUpResult.Task,
                ReceiptTimeout,
                "The active host did not finish peer World catch-up in time.",
                cancellationToken);
            if (result.WorldId != request.WorldId ||
                result.AuthorityGeneration != request.AuthorityGeneration ||
                result.CurrentStateRevisionId.Value == Guid.Empty)
            {
                throw new InvalidDataException(
                    "The active host returned a catch-up result for a different World, generation, or invalid state revision.");
            }

            return result;
        }
        finally
        {
            if (connection != HSteamNetConnection.Invalid)
            {
                await CloseConnectionAsync(
                    connection,
                    "Steward peer World catch-up request complete");
            }

            _outgoingGate.Release();
        }
    }

    private async Task<PeerWorldRevisionReceipt> TransferCoreAsync(
        UserIdentity targetPeer,
        PeerWorldRevisionOffer offer,
        Stream statePayload,
        TransferPurpose purpose,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetPeer);
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(statePayload);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!statePayload.CanRead)
        {
            throw new ArgumentException("Peer World payload must be readable.", nameof(statePayload));
        }

        var targetSteamId = ParseSteamIdentity(targetPeer);
        if (targetSteamId == _platform.LocalSteamId)
        {
            throw new InvalidOperationException("Steward cannot transfer a World to itself.");
        }

        await _outgoingGate.WaitAsync(cancellationToken);
        HSteamNetConnection connection = HSteamNetConnection.Invalid;
        try
        {
            var transferId = Guid.NewGuid();
            var created = await CreateOutgoingConnectionAsync(
                targetSteamId,
                transferId,
                cancellationToken);
            connection = created.Connection;
            var outgoing = created.Context;

            await WaitWithTimeoutAsync(
                outgoing.Connected.Task,
                ConnectTimeout,
                "Steam did not establish the peer World connection in time.",
                cancellationToken);

            var offerMessage = EncodeJson(
                MessageKind.Offer,
                new OfferEnvelope(
                    ProtocolVersion,
                    transferId,
                    purpose,
                    offer),
                MaxOfferBytes,
                "peer World offer");
            await SendReliableAsync(connection, offerMessage, cancellationToken);

            await WaitWithTimeoutAsync(
                outgoing.Ready.Task,
                ReadyTimeout,
                "The target did not authorize the peer World transfer in time.",
                cancellationToken);

            var buffer = new byte[PayloadChunkBytes];
            long sent = 0;
            while (true)
            {
                var read = await statePayload.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                checked
                {
                    sent += read;
                }

                if (sent > offer.PayloadLength)
                {
                    throw new InvalidDataException(
                        "The immutable source payload grew beyond the length Steward verified before transfer.");
                }

                var message = new byte[read + 1];
                message[0] = (byte)MessageKind.Payload;
                Buffer.BlockCopy(buffer, 0, message, 1, read);
                await SendReliableAsync(connection, message, cancellationToken);
            }

            if (sent != offer.PayloadLength)
            {
                throw new InvalidDataException(
                    $"The immutable source payload produced {sent} bytes during transfer, expected {offer.PayloadLength}.");
            }

            await SendReliableAsync(
                connection,
                EncodeJson(
                    MessageKind.Complete,
                    new TransferControlEnvelope(ProtocolVersion, transferId),
                    MaxControlBytes,
                    "peer World completion"),
                cancellationToken);
            await InvokeSteamAsync(
                () => SteamNetworkingSockets.FlushMessagesOnConnection(connection),
                cancellationToken);

            return await WaitWithTimeoutAsync(
                outgoing.Receipt.Task,
                ReceiptTimeout,
                "The target did not acknowledge the installed World in time.",
                cancellationToken);
        }
        finally
        {
            if (connection != HSteamNetConnection.Invalid)
            {
                await CloseConnectionAsync(connection, "Steward peer World transfer complete");
            }

            _outgoingGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _receiveTimer.Stop();
        _receiveTimer.Tick -= OnReceiveTick;
        _connectionCallback.Dispose();

        foreach (var context in _connections.Values.ToArray())
        {
            context.Fail(new ObjectDisposedException(nameof(SteamPeerWorldRevisionExchange)));
            _ = DisposeIncomingAsync(context.Incoming);
            SteamNetworkingSockets.CloseConnection(
                context.Connection,
                0,
                "Steward shutting down",
                false);
        }

        _connections.Clear();
        if (_listenSocket != HSteamListenSocket.Invalid)
        {
            SteamNetworkingSockets.CloseListenSocket(_listenSocket);
        }

        _outgoingGate.Dispose();
    }

    private async Task<(HSteamNetConnection Connection, OutgoingConnectionContext Context)>
        CreateOutgoingConnectionAsync(
            CSteamID targetSteamId,
            Guid transferId,
            CancellationToken cancellationToken)
    {
        return await InvokeSteamAsync(
            () =>
            {
                var identity = new SteamNetworkingIdentity();
                identity.Clear();
                identity.SetSteamID(targetSteamId);
                var connection = SteamNetworkingSockets.ConnectP2P(
                    ref identity,
                    VirtualPort,
                    0,
                    []);
                if (connection == HSteamNetConnection.Invalid)
                {
                    throw new IOException(
                        "Steam rejected the peer World P2P connection request.");
                }

                var outgoing = new OutgoingConnectionContext(transferId);
                _connections.Add(
                    connection,
                    new ConnectionContext(
                        connection,
                        targetSteamId.m_SteamID,
                        outgoing,
                        incoming: null));
                return (connection, outgoing);
            },
            cancellationToken);
    }

    private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t change)
    {
        if (_disposed)
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
            context.Outgoing?.Connected.TrySetResult(true);
            return;
        }

        if (state is
            ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer or
            ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally)
        {
            var exception = new IOException(
                $"Steam peer World connection ended: {change.m_info.m_szEndDebug}");
            RemoveConnection(connection, exception);
            SteamNetworkingSockets.CloseConnection(connection, 0, "Connection ended", false);
        }
    }

    private void AcceptIncomingConnection(SteamNetConnectionStatusChangedCallback_t change)
    {
        var remoteSteamId = change.m_info.m_identityRemote.GetSteamID64();
        if (remoteSteamId == 0 || remoteSteamId == _platform.LocalSteamId.m_SteamID)
        {
            SteamNetworkingSockets.CloseConnection(
                change.m_hConn,
                0,
                "Invalid Steward peer identity",
                false);
            return;
        }

        var incomingCount = _connections.Values.Count(static context => context.Incoming is not null);
        if (incomingCount >= MaxIncomingConnections)
        {
            SteamNetworkingSockets.CloseConnection(
                change.m_hConn,
                0,
                "Steward peer transfer capacity reached",
                false);
            return;
        }

        var accepted = SteamNetworkingSockets.AcceptConnection(change.m_hConn);
        if (accepted != EResult.k_EResultOK)
        {
            SteamNetworkingSockets.CloseConnection(
                change.m_hConn,
                0,
                "Steward could not accept peer transfer",
                false);
            return;
        }

        _connections[change.m_hConn] = new ConnectionContext(
            change.m_hConn,
            remoteSteamId,
            outgoing: null,
            new IncomingConnectionContext());
    }

    private void OnReceiveTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        foreach (var context in _connections.Values.ToArray())
        {
            try
            {
                DrainConnectionMessages(context);
            }
            catch (Exception exception)
            {
                _ = RejectAndCloseIncomingAsync(context, exception.Message);
            }
        }
    }

    private void DrainConnectionMessages(ConnectionContext context)
    {
        var maximumMessages = ReceiveBatchSize;
        if (context.Incoming?.Chunks is { } chunks && chunks.Reader.CanCount)
        {
            var availablePayloadSlots = MaxQueuedPayloadChunks - chunks.Reader.Count;
            if (availablePayloadSlots <= 0)
            {
                return;
            }

            maximumMessages = Math.Min(maximumMessages, availablePayloadSlots);
        }

        var pointers = new IntPtr[maximumMessages];
        var count = SteamNetworkingSockets.ReceiveMessagesOnConnection(
            context.Connection,
            pointers,
            pointers.Length);
        if (count < 0)
        {
            throw new IOException(
                "Steam reported an invalid peer World connection while receiving.");
        }

        for (var index = 0; index < count; index++)
        {
            var pointer = pointers[index];
            try
            {
                var native = SteamNetworkingMessage_t.FromIntPtr(pointer);
                if (native.m_cbSize <= 0 ||
                    native.m_cbSize > Constants.k_cbMaxSteamNetworkingSocketsMessageSizeSend)
                {
                    throw new InvalidDataException(
                        "Steam peer World message had an invalid size.");
                }

                var bytes = new byte[native.m_cbSize];
                Marshal.Copy(native.m_pData, bytes, 0, bytes.Length);
                DispatchMessage(context, bytes);
            }
            finally
            {
                SteamNetworkingMessage_t.Release(pointer);
                pointers[index] = IntPtr.Zero;
            }
        }
    }

    private void DispatchMessage(ConnectionContext context, byte[] message)
    {
        if (message.Length == 0)
        {
            throw new InvalidDataException("Steam peer World message was empty.");
        }

        var kind = (MessageKind)message[0];
        if (context.Outgoing is not null)
        {
            DispatchOutgoingMessage(context, kind, message);
            return;
        }

        if (context.Incoming is null)
        {
            throw new InvalidOperationException("Steam peer World connection has no direction state.");
        }

        DispatchIncomingMessage(context, kind, message);
    }

    private void DispatchOutgoingMessage(
        ConnectionContext context,
        MessageKind kind,
        byte[] message)
    {
        var outgoing = context.Outgoing!;
        switch (kind)
        {
            case MessageKind.Ready:
                {
                    var ready = DecodeJson<TransferControlEnvelope>(
                        message,
                        MaxControlBytes,
                        "peer World ready");
                    EnsureControl(ready.ProtocolVersion, ready.TransferId, outgoing.TransferId);
                    outgoing.Ready.TrySetResult(true);
                    break;
                }
            case MessageKind.Receipt:
                {
                    var receipt = DecodeJson<ReceiptEnvelope>(
                        message,
                        MaxControlBytes,
                        "peer World receipt");
                    EnsureControl(receipt.ProtocolVersion, receipt.TransferId, outgoing.TransferId);
                    outgoing.Receipt.TrySetResult(receipt.Receipt);
                    break;
                }
            case MessageKind.CatchUpResult:
                {
                    var result = DecodeJson<CatchUpResultEnvelope>(
                        message,
                        MaxControlBytes,
                        "peer World catch-up result");
                    EnsureControl(result.ProtocolVersion, result.TransferId, outgoing.TransferId);
                    outgoing.CatchUpResult.TrySetResult(result.Result);
                    break;
                }
            case MessageKind.Reject:
                {
                    var rejection = DecodeJson<RejectEnvelope>(
                        message,
                        MaxControlBytes,
                        "peer World rejection");
                    EnsureControl(rejection.ProtocolVersion, rejection.TransferId, outgoing.TransferId);
                    var exception = new InvalidOperationException(
                        string.IsNullOrWhiteSpace(rejection.Reason)
                            ? "The target rejected the peer World operation."
                            : rejection.Reason);
                    outgoing.Ready.TrySetException(exception);
                    outgoing.Receipt.TrySetException(exception);
                    outgoing.CatchUpResult.TrySetException(exception);
                    break;
                }
            default:
                throw new InvalidDataException(
                    $"Unexpected Steam peer World response kind '{kind}'.");
        }
    }

    private void DispatchIncomingMessage(
        ConnectionContext context,
        MessageKind kind,
        byte[] message)
    {
        var incoming = context.Incoming!;
        switch (kind)
        {
            case MessageKind.Offer when incoming.State == IncomingState.AwaitingOffer:
                {
                    var envelope = DecodeJson<OfferEnvelope>(
                        message,
                        MaxOfferBytes,
                        "peer World offer");
                    if (envelope.ProtocolVersion != ProtocolVersion ||
                        envelope.TransferId == Guid.Empty ||
                        !Enum.IsDefined(envelope.Purpose))
                    {
                        throw new InvalidDataException(
                            "Peer World offer uses an unsupported protocol version, transfer ID, or purpose.");
                    }

                    incoming.TransferId = envelope.TransferId;
                    incoming.Purpose = envelope.Purpose;
                    incoming.Offer = envelope.Offer;
                    incoming.State = IncomingState.Authorizing;
                    _ = AuthorizeIncomingOfferAsync(context, envelope);
                    break;
                }
            case MessageKind.CatchUpRequest when incoming.State == IncomingState.AwaitingOffer:
                {
                    var envelope = DecodeJson<CatchUpRequestEnvelope>(
                        message,
                        MaxControlBytes,
                        "peer World catch-up request");
                    if (envelope.ProtocolVersion != ProtocolVersion ||
                        envelope.TransferId == Guid.Empty ||
                        envelope.Request.WorldId.Value == Guid.Empty ||
                        envelope.Request.AuthorityGeneration == 0 ||
                        envelope.Request.LocalStateRevisionId is { } localRevision &&
                        localRevision.Value == Guid.Empty)
                    {
                        throw new InvalidDataException(
                            "Peer World catch-up request uses invalid protocol, request, World, generation, or revision metadata.");
                    }

                    incoming.TransferId = envelope.TransferId;
                    incoming.State = IncomingState.HandlingCatchUp;
                    _ = HandleCatchUpRequestAsync(context, envelope);
                    break;
                }
            case MessageKind.Payload when incoming.State == IncomingState.Receiving:
                {
                    if (message.Length <= 1 || message.Length > PayloadChunkBytes + 1)
                    {
                        throw new InvalidDataException("Peer World payload chunk has an invalid size.");
                    }

                    var chunk = message.AsSpan(1).ToArray();
                    if (!incoming.Chunks!.Writer.TryWrite(chunk))
                    {
                        throw new IOException(
                            "Peer World receiver could not preserve bounded payload backpressure.");
                    }

                    break;
                }
            case MessageKind.Complete when incoming.State == IncomingState.Receiving:
                {
                    var complete = DecodeJson<TransferControlEnvelope>(
                        message,
                        MaxControlBytes,
                        "peer World completion");
                    EnsureControl(complete.ProtocolVersion, complete.TransferId, incoming.TransferId);
                    incoming.State = IncomingState.Installing;
                    incoming.Chunks!.Writer.TryComplete();
                    _ = Task.Run(() => FinishIncomingAsync(context));
                    break;
                }
            default:
                throw new InvalidDataException(
                    $"Unexpected Steam peer World message kind '{kind}' while receiver is '{incoming.State}'.");
        }
    }

    private async Task HandleCatchUpRequestAsync(
        ConnectionContext context,
        CatchUpRequestEnvelope envelope)
    {
        try
        {
            var remoteExternalId = context.RemoteSteamId.ToString(CultureInfo.InvariantCulture);
            var remoteUser = new UserIdentity(
                "steam",
                remoteExternalId,
                remoteExternalId);
            var result = await _catchUpRouter.HandleAsync(
                remoteUser,
                envelope.Request,
                context.Incoming!.Cancellation.Token);

            if (!_connections.TryGetValue(context.Connection, out var current) ||
                !ReferenceEquals(current, context) ||
                current.Incoming is null ||
                current.Incoming.State != IncomingState.HandlingCatchUp)
            {
                return;
            }

            await SendReliableAsync(
                context.Connection,
                EncodeJson(
                    MessageKind.CatchUpResult,
                    new CatchUpResultEnvelope(
                        ProtocolVersion,
                        envelope.TransferId,
                        result),
                    MaxControlBytes,
                    "peer World catch-up result"),
                CancellationToken.None);
            await InvokeSteamAsync(
                () => SteamNetworkingSockets.FlushMessagesOnConnection(context.Connection),
                CancellationToken.None);

            current.Incoming.State = IncomingState.Completed;
            await CloseConnectionAsync(
                context.Connection,
                "Steward peer World catch-up complete",
                linger: true);
        }
        catch (Exception exception)
        {
            await RejectAndCloseIncomingAsync(context, exception.Message);
        }
    }

    private async Task AuthorizeIncomingOfferAsync(
        ConnectionContext context,
        OfferEnvelope envelope)
    {
        try
        {
            var offer = envelope.Offer;
            if (offer.PayloadLength < 0 ||
                offer.PayloadLength > PeerWorldRevisionReplicaInstaller.DefaultMaximumPayloadBytes)
            {
                throw new InvalidDataException(
                    "Peer World offer exceeds Steward's payload safety bound.");
            }

            var snapshot = await _lobby.GetAsync(offer.World.Id);
            if (snapshot is null ||
                !snapshot.OwnerConfirmed ||
                snapshot.AuthorityGeneration == 0 ||
                !SameSteamUser(snapshot.Owner, context.RemoteSteamId))
            {
                throw new InvalidOperationException(
                    "Peer World transfer is not authorized by the active Steward lobby host and generation.");
            }

            var authority = offer.World.PeerAuthority
                ?? throw new InvalidDataException(
                    "Peer World transfer offer is missing persistent authority.");
            if (authority.Generation == 0 ||
                !offer.World.Members.Any(member => SameUser(member, authority.Holder)))
            {
                throw new InvalidDataException(
                    "Peer World transfer offer contains invalid persistent authority.");
            }

            switch (envelope.Purpose)
            {
                case TransferPurpose.HandoffRevision:
                    if (snapshot.RequestedHost is null ||
                        !SameSteamUser(
                            snapshot.RequestedHost,
                            _platform.LocalSteamId.m_SteamID))
                    {
                        throw new InvalidOperationException(
                            "Peer handoff revision transfer is not addressed to this Steward participant.");
                    }

                    if (snapshot.AuthorityGeneration == ulong.MaxValue ||
                        authority.Generation != snapshot.AuthorityGeneration + 1 ||
                        !SameSteamUser(authority.Holder, _platform.LocalSteamId.m_SteamID) ||
                        !SameUser(authority.Holder, snapshot.RequestedHost))
                    {
                        throw new InvalidOperationException(
                            "Peer handoff revision does not carry the requested next holder at exactly the next authority generation.");
                    }

                    break;

                case TransferPurpose.Bootstrap:
                    EnsureCurrentAuthorityOffer(snapshot, authority, "bootstrap");
                    EnsureObserverAdmission(snapshot, offer, "bootstrap");
                    break;

                case TransferPurpose.ObserverSync:
                    EnsureCurrentAuthorityOffer(snapshot, authority, "observer synchronization");
                    EnsureObserverAdmission(snapshot, offer, "observer synchronization");
                    break;

                default:
                    throw new InvalidDataException(
                        $"Unsupported peer World transfer purpose '{envelope.Purpose}'.");
            }

            if (!_connections.TryGetValue(context.Connection, out var current) ||
                !ReferenceEquals(current, context) ||
                current.Incoming is null ||
                current.Incoming.State != IncomingState.Authorizing)
            {
                return;
            }

            var incoming = current.Incoming;
            incoming.Buffer = CreateIncomingBuffer();
            incoming.Chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaxQueuedPayloadChunks)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            incoming.WriterTask = Task.Run(() => WriteIncomingChunksAsync(incoming));
            incoming.State = IncomingState.Receiving;

            await SendReliableAsync(
                context.Connection,
                EncodeJson(
                    MessageKind.Ready,
                    new TransferControlEnvelope(ProtocolVersion, envelope.TransferId),
                    MaxControlBytes,
                    "peer World ready"),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            await RejectAndCloseIncomingAsync(context, exception.Message);
        }
    }

    private void EnsureCurrentAuthorityOffer(
        PeerWorldLobbySnapshot snapshot,
        WorldPeerAuthority authority,
        string purpose)
    {
        if (authority.Generation != snapshot.AuthorityGeneration ||
            !SameUser(authority.Holder, snapshot.Owner))
        {
            throw new InvalidOperationException(
                $"Peer {purpose} offer does not match the confirmed live holder and authority generation.");
        }
    }

    private void EnsureObserverAdmission(
        PeerWorldLobbySnapshot snapshot,
        PeerWorldRevisionOffer offer,
        string purpose)
    {
        if (snapshot.RequestedHost is not null)
        {
            throw new InvalidOperationException(
                $"Peer {purpose} is not allowed while the World is changing hosts.");
        }

        if (!offer.World.Members.Any(member =>
                SameSteamUser(member, _platform.LocalSteamId.m_SteamID)))
        {
            throw new InvalidOperationException(
                $"Peer {purpose} does not include the local Steam identity in canonical World membership.");
        }
    }

    private async Task WriteIncomingChunksAsync(IncomingConnectionContext incoming)
    {
        try
        {
            await foreach (var chunk in incoming.Chunks!.Reader.ReadAllAsync(incoming.Cancellation.Token))
            {
                checked
                {
                    incoming.ReceivedBytes += chunk.Length;
                }

                if (incoming.Offer is null || incoming.ReceivedBytes > incoming.Offer.PayloadLength)
                {
                    throw new InvalidDataException(
                        "Peer World payload exceeded the offered byte length.");
                }

                await incoming.Buffer!.WriteAsync(chunk, incoming.Cancellation.Token);
            }
        }
        catch (Exception exception)
        {
            incoming.WriterFailure = exception;
            throw;
        }
    }

    private async Task FinishIncomingAsync(ConnectionContext context)
    {
        var incoming = context.Incoming!;
        try
        {
            await incoming.WriterTask!;
            if (incoming.WriterFailure is not null)
            {
                throw incoming.WriterFailure;
            }

            var offer = incoming.Offer
                ?? throw new InvalidDataException("Peer World receiver lost its offer metadata.");
            if (incoming.ReceivedBytes != offer.PayloadLength)
            {
                throw new InvalidDataException(
                    $"Peer World receiver got {incoming.ReceivedBytes} payload bytes, expected {offer.PayloadLength}.");
            }

            await incoming.Buffer!.FlushAsync();
            incoming.Buffer.Position = 0;

            var receipt = incoming.Purpose switch
            {
                TransferPurpose.HandoffRevision =>
                    await _revisionInstaller.InstallAsync(
                        offer,
                        incoming.Buffer,
                        incoming.Cancellation.Token),
                TransferPurpose.Bootstrap =>
                    await _bootstrapInstaller.InstallAsync(
                        offer,
                        incoming.Buffer,
                        incoming.Cancellation.Token),
                TransferPurpose.ObserverSync =>
                    await _observerSyncInstaller.InstallAsync(
                        offer,
                        incoming.Buffer,
                        incoming.Cancellation.Token),
                _ => throw new InvalidDataException(
                    $"Unsupported peer World transfer purpose '{incoming.Purpose}'.")
            };

            await SendReliableAsync(
                context.Connection,
                EncodeJson(
                    MessageKind.Receipt,
                    new ReceiptEnvelope(
                        ProtocolVersion,
                        incoming.TransferId,
                        receipt),
                    MaxControlBytes,
                    "peer World receipt"),
                CancellationToken.None);
            await InvokeSteamAsync(
                () => SteamNetworkingSockets.FlushMessagesOnConnection(context.Connection),
                CancellationToken.None);

            incoming.State = IncomingState.Completed;
            await CloseConnectionAsync(
                context.Connection,
                "Steward peer World installed",
                linger: true);
        }
        catch (Exception exception)
        {
            await RejectAndCloseIncomingAsync(context, exception.Message);
        }
    }

    private async Task RejectAndCloseIncomingAsync(
        ConnectionContext context,
        string reason)
    {
        if (context.Incoming is null)
        {
            RemoveConnection(context.Connection, new InvalidDataException(reason));
            await CloseConnectionAsync(context.Connection, reason);
            return;
        }

        var incoming = context.Incoming;
        if (incoming.State == IncomingState.Completed || incoming.State == IncomingState.Failed)
        {
            return;
        }

        incoming.State = IncomingState.Failed;
        incoming.Cancellation.Cancel();
        incoming.Chunks?.Writer.TryComplete();

        var safeReason = BoundUtf8(reason, MaxRejectTextBytes);
        try
        {
            if (incoming.TransferId != Guid.Empty)
            {
                await SendReliableAsync(
                    context.Connection,
                    EncodeJson(
                        MessageKind.Reject,
                        new RejectEnvelope(
                            ProtocolVersion,
                            incoming.TransferId,
                            safeReason),
                        MaxControlBytes,
                        "peer World rejection"),
                    CancellationToken.None);
                await InvokeSteamAsync(
                    () => SteamNetworkingSockets.FlushMessagesOnConnection(context.Connection),
                    CancellationToken.None);
            }
        }
        catch
        {
        }

        await CloseConnectionAsync(context.Connection, safeReason, linger: true);
    }

    private async Task SendReliableAsync(
        HSteamNetConnection connection,
        byte[] message,
        CancellationToken cancellationToken)
    {
        if (message.Length <= 0 ||
            message.Length > Constants.k_cbMaxSteamNetworkingSocketsMessageSizeSend)
        {
            throw new InvalidDataException(
                $"Steward peer World message length '{message.Length}' is outside Steam's supported range.");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await InvokeSteamAsync(
                () => SendPinned(connection, message),
                cancellationToken);
            if (result == EResult.k_EResultOK)
            {
                return;
            }

            if (result == EResult.k_EResultLimitExceeded)
            {
                await Task.Delay(SendBackpressureDelay, cancellationToken);
                continue;
            }

            throw new IOException(
                $"Steam rejected a reliable Steward peer World message: {result}.");
        }
    }

    private static EResult SendPinned(
        HSteamNetConnection connection,
        byte[] message)
    {
        var pinned = GCHandle.Alloc(message, GCHandleType.Pinned);
        try
        {
            return SteamNetworkingSockets.SendMessageToConnection(
                connection,
                pinned.AddrOfPinnedObject(),
                checked((uint)message.Length),
                Constants.k_nSteamNetworkingSend_ReliableNoNagle,
                out _);
        }
        finally
        {
            pinned.Free();
        }
    }

    private async Task CloseConnectionAsync(
        HSteamNetConnection connection,
        string reason,
        bool linger = false)
    {
        ConnectionContext? removed = null;
        await InvokeSteamAsync(
            () =>
            {
                if (_connections.Remove(connection, out var context))
                {
                    removed = context;
                }

                SteamNetworkingSockets.CloseConnection(
                    connection,
                    0,
                    BoundUtf8(
                        reason,
                        Constants.k_cchSteamNetworkingMaxConnectionCloseReason - 1),
                    linger);
            },
            CancellationToken.None);

        if (removed?.Incoming is not null)
        {
            await DisposeIncomingAsync(removed.Incoming);
        }
    }

    private void RemoveConnection(
        HSteamNetConnection connection,
        Exception exception)
    {
        if (!_connections.Remove(connection, out var context))
        {
            return;
        }

        context.Fail(exception);
        _ = DisposeIncomingAsync(context.Incoming);
    }

    private static async Task DisposeIncomingAsync(IncomingConnectionContext? incoming)
    {
        if (incoming is null)
        {
            return;
        }

        incoming.Cancellation.Cancel();
        incoming.Chunks?.Writer.TryComplete();
        if (incoming.WriterTask is not null)
        {
            try
            {
                await incoming.WriterTask;
            }
            catch
            {
            }
        }

        if (incoming.Buffer is not null)
        {
            await incoming.Buffer.DisposeAsync();
        }

        incoming.Cancellation.Dispose();
    }

    private static FileStream CreateIncomingBuffer()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "SharedWorlds",
            "peer-world-transfer");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"{Guid.NewGuid():N}.tmp");
        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 128 * 1024,
            options: FileOptions.Asynchronous |
                     FileOptions.SequentialScan |
                     FileOptions.DeleteOnClose);
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
            DispatcherPriority.Normal,
            cancellationToken).Task;
    }

    private async Task InvokeSteamAsync(
        Action action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        if (_platform.Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        await _platform.Dispatcher.InvokeAsync(
            action,
            DispatcherPriority.Normal,
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
                "A valid Steam UserIdentity is required for Steam peer World transfer.",
                nameof(user));
        }

        return new CSteamID(steamId);
    }

    private static bool SameSteamUser(UserIdentity user, ulong steamId)
        => string.Equals(user.Provider, "steam", StringComparison.OrdinalIgnoreCase) &&
           ulong.TryParse(
               user.ExternalId,
               NumberStyles.None,
               CultureInfo.InvariantCulture,
               out var parsed) &&
           parsed == steamId;

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);

    private static byte[] EncodeJson<T>(
        MessageKind kind,
        T value,
        int maximumBytes,
        string description)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value);
        if (json.Length + 1 > maximumBytes)
        {
            throw new InvalidDataException(
                $"Serialized {description} exceeds Steward's {maximumBytes}-byte bound.");
        }

        var message = new byte[json.Length + 1];
        message[0] = (byte)kind;
        Buffer.BlockCopy(json, 0, message, 1, json.Length);
        return message;
    }

    private static T DecodeJson<T>(
        byte[] message,
        int maximumBytes,
        string description)
    {
        if (message.Length <= 1 || message.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"Serialized {description} is outside Steward's accepted size bound.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(message.AsSpan(1))
                ?? throw new InvalidDataException($"Serialized {description} was empty.");
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
        Guid actualTransferId,
        Guid expectedTransferId)
    {
        if (protocolVersion != ProtocolVersion || actualTransferId != expectedTransferId)
        {
            throw new InvalidDataException(
                "Steam peer World control message does not match the active operation.");
        }
    }

    private static string BoundUtf8(string? text, int maximumBytes)
    {
        var value = string.IsNullOrWhiteSpace(text)
            ? "Steward peer World operation failed."
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
            ? "Steward peer operation failed."
            : builder.ToString();
    }

    private enum TransferPurpose : byte
    {
        HandoffRevision = 1,
        Bootstrap = 2,
        ObserverSync = 3
    }

    private enum MessageKind : byte
    {
        Offer = 1,
        Ready = 2,
        Payload = 3,
        Complete = 4,
        Receipt = 5,
        Reject = 6,
        CatchUpRequest = 7,
        CatchUpResult = 8
    }

    private enum IncomingState
    {
        AwaitingOffer = 0,
        Authorizing = 1,
        Receiving = 2,
        Installing = 3,
        HandlingCatchUp = 4,
        Completed = 5,
        Failed = 6
    }

    private sealed record OfferEnvelope(
        int ProtocolVersion,
        Guid TransferId,
        TransferPurpose Purpose,
        PeerWorldRevisionOffer Offer);

    private sealed record TransferControlEnvelope(
        int ProtocolVersion,
        Guid TransferId);

    private sealed record ReceiptEnvelope(
        int ProtocolVersion,
        Guid TransferId,
        PeerWorldRevisionReceipt Receipt);

    private sealed record CatchUpRequestEnvelope(
        int ProtocolVersion,
        Guid TransferId,
        PeerWorldCatchUpRequest Request);

    private sealed record CatchUpResultEnvelope(
        int ProtocolVersion,
        Guid TransferId,
        PeerWorldCatchUpResult Result);

    private sealed record RejectEnvelope(
        int ProtocolVersion,
        Guid TransferId,
        string Reason);

    private sealed class ConnectionContext
    {
        public ConnectionContext(
            HSteamNetConnection connection,
            ulong remoteSteamId,
            OutgoingConnectionContext? outgoing,
            IncomingConnectionContext? incoming)
        {
            Connection = connection;
            RemoteSteamId = remoteSteamId;
            Outgoing = outgoing;
            Incoming = incoming;
        }

        public HSteamNetConnection Connection { get; }
        public ulong RemoteSteamId { get; }
        public OutgoingConnectionContext? Outgoing { get; }
        public IncomingConnectionContext? Incoming { get; }

        public void Fail(Exception exception)
        {
            Outgoing?.Connected.TrySetException(exception);
            Outgoing?.Ready.TrySetException(exception);
            Outgoing?.Receipt.TrySetException(exception);
            Outgoing?.CatchUpResult.TrySetException(exception);
            Incoming?.Cancellation.Cancel();
            Incoming?.Chunks?.Writer.TryComplete(exception);
        }
    }

    private sealed class OutgoingConnectionContext
    {
        public OutgoingConnectionContext(Guid transferId)
            => TransferId = transferId;

        public Guid TransferId { get; }
        public TaskCompletionSource<bool> Connected { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Ready { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<PeerWorldRevisionReceipt> Receipt { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<PeerWorldCatchUpResult> CatchUpResult { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class IncomingConnectionContext
    {
        public IncomingState State { get; set; } = IncomingState.AwaitingOffer;
        public Guid TransferId { get; set; }
        public TransferPurpose Purpose { get; set; }
        public PeerWorldRevisionOffer? Offer { get; set; }
        public FileStream? Buffer { get; set; }
        public Channel<byte[]>? Chunks { get; set; }
        public Task? WriterTask { get; set; }
        public Exception? WriterFailure { get; set; }
        public long ReceivedBytes { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
    }
}
