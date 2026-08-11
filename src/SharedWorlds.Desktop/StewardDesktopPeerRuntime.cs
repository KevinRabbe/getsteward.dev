using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Sessions;

namespace SharedWorlds.Desktop;

/// <summary>
/// Process-local composition root for Steward's peer-hosted shared-World runtime. This runtime does not
/// own Steam initialization or shutdown: every Steam-backed component receives the one process-lifetime
/// <see cref="SteamPlatformRuntime"/> already owned by <see cref="App"/>.
///
/// The runtime deliberately contains only active peer gameplay concerns: local canonical storage with
/// durable authority fencing, live lobby authority, exact revision bootstrap/handoff/observer transfer,
/// initial peer sharing, managed-host presence, membership, live revocation fencing, authenticated
/// Leave World control, Steam friend discovery, invitations, and the Steam game-data bridge. Central
/// remote services and remote object storage are not part of this composition.
/// </summary>
internal sealed class StewardDesktopPeerRuntime : IDisposable
{
    private readonly SteamPlatformRuntime _platform;
    private readonly SteamPeerWorldLobby _lobby;
    private readonly PeerManagedHostPresenceRegistry _hostPresence;
    private readonly PeerWorldLiveMemberRevocationRegistry _liveMemberRevocations;
    private readonly PeerWorldLiveAuthorityMutationGate _liveAuthorityMutations;
    private readonly PeerGameDatagramBridgeAdmissionService _gameBridgeAdmission;
    private readonly SteamPeerWorldRevisionExchange _revisionExchange;
    private readonly SteamPeerWorldLobbyJoinService _lobbyJoin;
    private readonly object _gameBridgeGate = new();
    private SteamPeerGameDatagramBridge? _gameBridge;
    private Exception? _gameBridgeProblem;
    private int _disposed;

    private StewardDesktopPeerRuntime(
        SteamPlatformRuntime platform,
        IWorldStorage storage,
        WorldLifecycleService lifecycle,
        UserIdentity user,
        SteamPeerWorldLobby lobby,
        SteamPeerWorldLobbyJoinService lobbyJoin,
        PeerWorldBootstrapTransferService bootstrap,
        PeerWorldObserverSyncService observerSync,
        PeerWorldInitialShareService initialShare,
        PeerWorldMembershipService membership,
        PeerWorldMemberRemovalService memberRemoval,
        PeerWorldMemberInvitationService invitations,
        IWorldSessionCoordinator sessionCoordinator,
        PeerManagedHostPresenceRegistry hostPresence,
        PeerWorldLiveMemberRevocationRegistry liveMemberRevocations,
        PeerWorldLiveAuthorityMutationGate liveAuthorityMutations,
        IPeerAuthorityActiveRevisionFenceStore authorityFences,
        IPeerWorldCatchUpRequestClient catchUp,
        IPeerWorldLeaveRequestClient leaveRequests,
        SteamPeerWorldRevisionExchange revisionExchange,
        PeerGameDatagramBridgeAdmissionService gameBridgeAdmission,
        SteamPeerGameDatagramBridge gameBridge)
    {
        _platform = platform;
        _lobby = lobby;
        _hostPresence = hostPresence;
        _liveMemberRevocations = liveMemberRevocations;
        _liveAuthorityMutations = liveAuthorityMutations;
        _gameBridgeAdmission = gameBridgeAdmission;
        _revisionExchange = revisionExchange;
        _lobbyJoin = lobbyJoin;
        _gameBridge = gameBridge;

        Storage = storage;
        Lifecycle = lifecycle;
        User = user;
        Friends = new SteamFriendDirectory(platform);
        Lobby = lobby;
        LobbyJoin = lobbyJoin;
        Bootstrap = bootstrap;
        ObserverSync = observerSync;
        InitialShare = initialShare;
        Membership = membership;
        MemberRemoval = memberRemoval;
        Invitations = invitations;
        SessionCoordinator = sessionCoordinator;
        HostPresence = hostPresence;
        LiveMemberRevocations = liveMemberRevocations;
        AuthorityFences = authorityFences;
        CatchUp = catchUp;
        LeaveRequests = leaveRequests;

        _hostPresence.Ended += OnManagedHostPresenceEnded;
    }

    public IWorldStorage Storage { get; }
    public WorldLifecycleService Lifecycle { get; }
    public UserIdentity User { get; }
    public SteamFriendDirectory Friends { get; }
    public SteamPeerWorldLobby Lobby { get; }
    public SteamPeerWorldLobbyJoinService LobbyJoin { get; }
    public PeerWorldBootstrapTransferService Bootstrap { get; }
    public PeerWorldObserverSyncService ObserverSync { get; }
    public PeerWorldInitialShareService InitialShare { get; }
    public PeerWorldMembershipService Membership { get; }
    public PeerWorldMemberRemovalService MemberRemoval { get; }
    public PeerWorldMemberInvitationService Invitations { get; }
    public IWorldSessionCoordinator SessionCoordinator { get; }
    public IPeerManagedHostPresenceRegistry HostPresence { get; }
    public PeerWorldLiveMemberRevocationRegistry LiveMemberRevocations { get; }
    public IPeerAuthorityActiveRevisionFenceStore AuthorityFences { get; }
    public IPeerWorldCatchUpRequestClient CatchUp { get; }
    public IPeerWorldLeaveRequestClient LeaveRequests { get; }

    public SteamPeerGameDatagramBridge GameBridge
    {
        get
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            lock (_gameBridgeGate)
            {
                return _gameBridge
                    ?? throw new InvalidOperationException(
                        "Steward's peer game bridge is unavailable after managed-host teardown.",
                        _gameBridgeProblem);
            }
        }
    }

    public static StewardDesktopPeerRuntime Create(
        SteamPlatformRuntime platform,
        string installationId,
        IWorldStorage localStorage,
        IWorkspaceRecoveryStore recoveryStore,
        ManagedWritableSessionGate managedSessionGate,
        IWorldLifecycleObserver lifecycleObserver)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        ArgumentNullException.ThrowIfNull(localStorage);
        ArgumentNullException.ThrowIfNull(recoveryStore);
        ArgumentNullException.ThrowIfNull(managedSessionGate);
        ArgumentNullException.ThrowIfNull(lifecycleObserver);
        if (!platform.Dispatcher.CheckAccess())
        {
            throw new InvalidOperationException(
                "Steward peer runtime must be composed on the process Steam dispatcher.");
        }

        var user = platform.LocalUser;
        var authorityFences = new SteamCloudPeerAuthorityFenceStore(
            platform,
            installationId);
        var storage = new PeerAuthorityFencedWorldStorage(
            localStorage,
            authorityFences,
            user);
        var lobby = new SteamPeerWorldLobby(platform);
        var liveMemberRevocations = new PeerWorldLiveMemberRevocationRegistry();
        var liveAuthorityMutations = new PeerWorldLiveAuthorityMutationGate();
        var membership = new PeerWorldMembershipService(
            storage,
            authorityFences,
            liveMemberRevocations,
            liveAuthorityMutations);
        var memberRemoval = new PeerWorldMemberRemovalService(
            storage,
            authorityFences,
            lobby,
            liveMemberRevocations,
            liveAuthorityMutations);
        var leaveRouter = new PeerWorldLeaveRequestRouter(
            storage,
            lobby,
            memberRemoval,
            user);

        SteamPeerWorldRevisionExchange? revisionExchange = null;
        SteamPeerWorldLobbyJoinService? lobbyJoin = null;
        SteamPeerGameDatagramBridge? gameBridge = null;
        try
        {
            var revisionInstaller = new PeerWorldRevisionReplicaInstaller(
                storage,
                user,
                authorityFences);
            var bootstrapInstaller = new PeerWorldBootstrapInstaller(
                storage,
                user,
                authorityFences);
            var observerInstaller = new PeerWorldObserverSyncInstaller(
                storage,
                user,
                authorityFences);
            var catchUpRouter = new PeerWorldCatchUpRequestRouter(
                storage,
                lobby,
                authorityFences,
                user,
                liveMemberRevocations);

            revisionExchange = new SteamPeerWorldRevisionExchange(
                platform,
                lobby,
                revisionInstaller,
                bootstrapInstaller,
                observerInstaller,
                catchUpRouter,
                leaveRouter);

            var generationBoundExchange = new GenerationBoundPeerWorldExchange(
                lobby,
                user,
                revisionExchange,
                revisionExchange);
            var revisionTransfer = new PeerWorldRevisionTransferService(
                storage,
                generationBoundExchange);
            var authorityCoordinator = new PeerWorldSessionCoordinator(
                lobby,
                user,
                revisionTransfer,
                storage,
                authorityFences);
            var liveAuthorityCoordinator = new PeerWorldLiveAuthoritySessionCoordinator(
                authorityCoordinator,
                storage,
                liveMemberRevocations,
                liveAuthorityMutations);
            var hostPresence = new PeerManagedHostPresenceRegistry();
            var presenceCoordinator = new PeerManagedHostPresenceSessionCoordinator(
                liveAuthorityCoordinator,
                storage,
                hostPresence,
                user);
            var invitations = new PeerWorldMemberInvitationService(
                storage,
                authorityFences,
                lobby);
            var sessionCoordinator = new PeerWorldMemberInvitationSessionCoordinator(
                presenceCoordinator,
                invitations,
                user);
            var lifecycle = new WorldLifecycleService(
                storage,
                sessionCoordinator,
                recoveryStore,
                managedSessionGate,
                lifecycleObserver);

            var bootstrap = new PeerWorldBootstrapTransferService(
                storage,
                generationBoundExchange);
            var observerExchange = new GenerationBoundPeerWorldObserverSyncExchange(
                lobby,
                user,
                revisionExchange);
            var observerSync = new PeerWorldObserverSyncService(
                storage,
                user,
                authorityFences,
                observerExchange);
            catchUpRouter.Bind(
                bootstrap,
                observerSync);
            var initialShare = new PeerWorldInitialShareService(
                storage,
                authorityFences);

            lobbyJoin = new SteamPeerWorldLobbyJoinService(
                platform,
                lobby);
            var gameBridgeAdmission = new PeerGameDatagramBridgeAdmissionService(
                lobby,
                storage,
                hostPresence,
                user,
                liveMemberRevocations);
            gameBridge = new SteamPeerGameDatagramBridge(
                platform,
                lobby,
                gameBridgeAdmission,
                liveMemberRevocations);

            return new StewardDesktopPeerRuntime(
                platform,
                storage,
                lifecycle,
                user,
                lobby,
                lobbyJoin,
                bootstrap,
                observerSync,
                initialShare,
                membership,
                memberRemoval,
                invitations,
                sessionCoordinator,
                hostPresence,
                liveMemberRevocations,
                liveAuthorityMutations,
                authorityFences,
                revisionExchange,
                revisionExchange,
                revisionExchange,
                gameBridgeAdmission,
                gameBridge);
        }
        catch
        {
            gameBridge?.Dispose();
            lobbyJoin?.Dispose();
            revisionExchange?.Dispose();
            liveMemberRevocations.Dispose();
            liveAuthorityMutations.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Adds process-local Starting/Ready/End publication to an adapter that exposes a managed host
    /// endpoint. The game adapter still owns all game processes and save semantics; peer session
    /// coordination only owns joinability evidence for the already-authoritative host.
    /// </summary>
    public IGameAdapter CoordinateManagedHost(
        WorldId worldId,
        IGameAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        return adapter is IManagedHostEndpointProvider endpointProvider
            ? new CoordinatedHostGameAdapter(
                adapter,
                endpointProvider,
                SessionCoordinator,
                worldId)
            : adapter;
    }

    private void OnManagedHostPresenceEnded(PeerManagedHostPresence ended)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // ManagedWritableSessionGate permits only one writable managed host lifecycle in this Steward
        // process. Resetting the whole data-plane listener therefore revokes every bridge that could
        // belong to the ended host tuple without adding per-packet authority polling to port 72.
        SteamPeerGameDatagramBridge? retiring;
        lock (_gameBridgeGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            retiring = _gameBridge;
            _gameBridge = null;
            _gameBridgeProblem = null;
        }

        // Close stale host-side sessions immediately before a fresh listener is created. The old live
        // member cancellation keys are then unnecessary: canonical membership will govern any later
        // host lifecycle, including a restart at the same persistent authority generation.
        retiring?.Dispose();
        _liveMemberRevocations.ClearWorld(
            ended.WorldId,
            ended.AuthorityGeneration);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            if (_platform.Dispatcher.CheckAccess())
            {
                RestoreGameBridge();
            }
            else
            {
                _platform.Dispatcher.Invoke(RestoreGameBridge);
            }
        }
        catch (Exception exception)
        {
            RecordGameBridgeProblem(exception);
        }
    }

    private void RestoreGameBridge()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        SteamPeerGameDatagramBridge? replacement = null;
        try
        {
            replacement = new SteamPeerGameDatagramBridge(
                _platform,
                _lobby,
                _gameBridgeAdmission,
                _liveMemberRevocations);
            lock (_gameBridgeGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    replacement.Dispose();
                    return;
                }

                if (_gameBridge is null)
                {
                    _gameBridge = replacement;
                    _gameBridgeProblem = null;
                    replacement = null;
                }
            }
        }
        catch (Exception exception)
        {
            RecordGameBridgeProblem(exception);
        }
        finally
        {
            replacement?.Dispose();
        }
    }

    private void RecordGameBridgeProblem(Exception exception)
    {
        lock (_gameBridgeGate)
        {
            if (Volatile.Read(ref _disposed) == 0 && _gameBridge is null)
            {
                _gameBridgeProblem = exception;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _hostPresence.Ended -= OnManagedHostPresenceEnded;
        SteamPeerGameDatagramBridge? gameBridge;
        lock (_gameBridgeGate)
        {
            gameBridge = _gameBridge;
            _gameBridge = null;
        }

        // Stop live game traffic first, then future lobby admission, then World transfer/control traffic
        // and finally the process-local cancellation/mutation fences. SteamPlatformRuntime remains owned by App.
        gameBridge?.Dispose();
        _lobbyJoin.Dispose();
        _revisionExchange.Dispose();
        _liveMemberRevocations.Dispose();
        _liveAuthorityMutations.Dispose();
    }
}
