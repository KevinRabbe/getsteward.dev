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
/// managed-host presence, membership, and the Steam game-data bridge. Central remote services and
/// remote object storage are not part of this composition.
/// </summary>
internal sealed class StewardDesktopPeerRuntime : IDisposable
{
    private readonly SteamPeerWorldRevisionExchange _revisionExchange;
    private readonly SteamPeerWorldLobbyJoinService _lobbyJoin;
    private readonly SteamPeerGameDatagramBridge _gameBridge;
    private int _disposed;

    private StewardDesktopPeerRuntime(
        IWorldStorage storage,
        WorldLifecycleService lifecycle,
        UserIdentity user,
        SteamPeerWorldLobby lobby,
        SteamPeerWorldLobbyJoinService lobbyJoin,
        PeerWorldBootstrapTransferService bootstrap,
        PeerWorldObserverSyncService observerSync,
        PeerWorldMembershipService membership,
        IWorldSessionCoordinator sessionCoordinator,
        IPeerManagedHostPresenceRegistry hostPresence,
        IPeerAuthorityActiveRevisionFenceStore authorityFences,
        IPeerWorldCatchUpRequestClient catchUp,
        SteamPeerWorldRevisionExchange revisionExchange,
        SteamPeerGameDatagramBridge gameBridge)
    {
        Storage = storage;
        Lifecycle = lifecycle;
        User = user;
        Lobby = lobby;
        LobbyJoin = lobbyJoin;
        Bootstrap = bootstrap;
        ObserverSync = observerSync;
        Membership = membership;
        SessionCoordinator = sessionCoordinator;
        HostPresence = hostPresence;
        AuthorityFences = authorityFences;
        CatchUp = catchUp;
        GameBridge = gameBridge;
        _revisionExchange = revisionExchange;
        _lobbyJoin = lobbyJoin;
        _gameBridge = gameBridge;
    }

    public IWorldStorage Storage { get; }
    public WorldLifecycleService Lifecycle { get; }
    public UserIdentity User { get; }
    public SteamPeerWorldLobby Lobby { get; }
    public SteamPeerWorldLobbyJoinService LobbyJoin { get; }
    public PeerWorldBootstrapTransferService Bootstrap { get; }
    public PeerWorldObserverSyncService ObserverSync { get; }
    public PeerWorldMembershipService Membership { get; }
    public IWorldSessionCoordinator SessionCoordinator { get; }
    public IPeerManagedHostPresenceRegistry HostPresence { get; }
    public IPeerAuthorityActiveRevisionFenceStore AuthorityFences { get; }
    public IPeerWorldCatchUpRequestClient CatchUp { get; }
    public SteamPeerGameDatagramBridge GameBridge { get; }

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
                user);

            revisionExchange = new SteamPeerWorldRevisionExchange(
                platform,
                lobby,
                revisionInstaller,
                bootstrapInstaller,
                observerInstaller,
                catchUpRouter);

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
            var hostPresence = new PeerManagedHostPresenceRegistry();
            var sessionCoordinator = new PeerManagedHostPresenceSessionCoordinator(
                authorityCoordinator,
                storage,
                hostPresence,
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
            var membership = new PeerWorldMembershipService(
                storage,
                authorityFences);

            lobbyJoin = new SteamPeerWorldLobbyJoinService(
                platform,
                lobby);
            var gameBridgeAdmission = new PeerGameDatagramBridgeAdmissionService(
                lobby,
                storage,
                hostPresence,
                user);
            gameBridge = new SteamPeerGameDatagramBridge(
                platform,
                lobby,
                gameBridgeAdmission);

            return new StewardDesktopPeerRuntime(
                storage,
                lifecycle,
                user,
                lobby,
                lobbyJoin,
                bootstrap,
                observerSync,
                membership,
                sessionCoordinator,
                hostPresence,
                authorityFences,
                revisionExchange,
                revisionExchange,
                gameBridge);
        }
        catch
        {
            gameBridge?.Dispose();
            lobbyJoin?.Dispose();
            revisionExchange?.Dispose();
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Stop live game traffic first, then future lobby admission, then World transfer traffic.
        // SteamPlatformRuntime remains owned by App and is intentionally never disposed here.
        _gameBridge.Dispose();
        _lobbyJoin.Dispose();
        _revisionExchange.Dispose();
    }
}
