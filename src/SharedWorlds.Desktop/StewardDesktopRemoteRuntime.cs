using System.IO;
using System.Net.Http;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Desktop;

/// <summary>
/// Owns the authenticated shared-World runtime used by the Windows desktop after external identity has
/// already been verified. Once authenticated, all shared World metadata, transfer, authority, commit,
/// recovery, flat access-management, host-presence, ephemeral player-presence, and private owned-World
/// location publication/discovery traffic flows through remote Infrastructure.
/// </summary>
internal sealed class StewardDesktopRemoteRuntime : IDisposable
{
    private readonly HttpClient _apiClient;
    private readonly HttpClient _transferClient;
    private readonly StewardWritableReservationRegistry _reservations;
    private readonly StewardAccessSession _accessSession;
    private readonly StewardWorldMetadataClient _metadata;
    private readonly StewardHostPresenceClient _hostPresence;
    private readonly StewardWorldSessionCoordinator _coordinator;
    private readonly StewardOwnedWorldLocationPublicationService _ownedWorldLocationPublication;
    private readonly StewardOwnedWorldLocationCatalogReconciler _ownedWorldLocationCatalogReconciler;
    private readonly StewardOwnedWorldSnapshotPublisher _ownedWorldSnapshotPublisher;
    private readonly StewardOwnedPrivateWorldCatalogClient _ownedPrivateWorldCatalog;
    private bool _disposed;

    private StewardDesktopRemoteRuntime(
        HttpClient apiClient,
        HttpClient transferClient,
        StewardWritableReservationRegistry reservations,
        StewardAccessSession accessSession,
        StewardWorldMetadataClient metadata,
        StewardHostPresenceClient hostPresence,
        StewardWorldSessionCoordinator coordinator,
        StewardOwnedWorldLocationPublicationService ownedWorldLocationPublication,
        StewardOwnedWorldLocationCatalogReconciler ownedWorldLocationCatalogReconciler,
        StewardOwnedWorldSnapshotPublisher ownedWorldSnapshotPublisher,
        StewardOwnedPrivateWorldCatalogClient ownedPrivateWorldCatalog,
        StewardWorldStorage storage,
        WorldLifecycleService lifecycle,
        CoordinatedWorldJoinService join,
        StewardPendingSyncRecoveryService pendingSyncRecovery,
        StewardInitialWorldPublisher initialWorldPublisher,
        StewardWorldAccessClient access,
        StewardWorldPlayerPresenceClient playerPresence,
        StewardOwnedWorldLocationClient ownedWorldLocations,
        UserIdentity user)
    {
        _apiClient = apiClient;
        _transferClient = transferClient;
        _reservations = reservations;
        _accessSession = accessSession;
        _metadata = metadata;
        _hostPresence = hostPresence;
        _coordinator = coordinator;
        _ownedWorldLocationPublication = ownedWorldLocationPublication;
        _ownedWorldLocationCatalogReconciler = ownedWorldLocationCatalogReconciler;
        _ownedWorldSnapshotPublisher = ownedWorldSnapshotPublisher;
        _ownedPrivateWorldCatalog = ownedPrivateWorldCatalog;
        Storage = storage;
        Lifecycle = lifecycle;
        Join = join;
        PendingSyncRecovery = pendingSyncRecovery;
        InitialWorldPublisher = initialWorldPublisher;
        Access = access;
        PlayerPresence = playerPresence;
        OwnedWorldLocations = ownedWorldLocations;
        User = user;
    }

    public StewardWorldStorage Storage { get; }
    public WorldLifecycleService Lifecycle { get; }
    public CoordinatedWorldJoinService Join { get; }
    public StewardPendingSyncRecoveryService PendingSyncRecovery { get; }
    public StewardInitialWorldPublisher InitialWorldPublisher { get; }
    public StewardWorldAccessClient Access { get; }
    public StewardWorldPlayerPresenceClient PlayerPresence { get; }
    public StewardOwnedWorldLocationClient OwnedWorldLocations { get; }
    public UserIdentity User { get; }

    public Task<IReadOnlyList<StewardOwnedPrivateWorldCatalogEntry>>
        ListOwnedPrivateWorldsAsync(CancellationToken cancellationToken = default)
        => _ownedPrivateWorldCatalog.ListAsync(cancellationToken);

    public async Task ReconcileAndReplayOwnedWorldLocationsAsync(
        CancellationToken cancellationToken = default)
    {
        await _ownedWorldLocationCatalogReconciler.ReconcileAsync(cancellationToken);
        await _ownedWorldLocationPublication.ReplayAllAsync(cancellationToken);
    }

    public Task PublishCurrentOwnedWorldSnapshotsAsync(
        CancellationToken cancellationToken = default)
        => _ownedWorldSnapshotPublisher.PublishAllCurrentAsync(cancellationToken);

    public async Task<StewardRemoteWorldMetadata?> GetWorldMetadataAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        var accessToken = await _accessSession.GetAccessTokenAsync(cancellationToken);
        return await _metadata.GetWorldAsync(worldId, accessToken, cancellationToken);
    }

    public async Task<StewardRemoteHostPresence?> GetHostPresenceAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        var accessToken = await _accessSession.GetAccessTokenAsync(cancellationToken);
        return await _hostPresence.GetAsync(worldId, accessToken, cancellationToken);
    }

    public IGameAdapter CoordinateManagedHost(WorldId worldId, IGameAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        return adapter is IManagedHostEndpointProvider endpointProvider
            ? new CoordinatedHostGameAdapter(adapter, endpointProvider, _coordinator, worldId)
            : adapter;
    }

    public static StewardDesktopRemoteRuntime Create(
        Uri apiBaseAddress,
        string installationId,
        StewardRemoteSessionTokens initialTokens,
        UserIdentity authenticatedUser,
        string dataRoot,
        IWorldStorage localStorage,
        IOwnedWorldLocationPublicationJournal ownedWorldLocationPublicationJournal,
        IWorkspaceRecoveryStore recoveryStore,
        IWorldLifecycleObserver lifecycleObserver)
    {
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        ArgumentNullException.ThrowIfNull(initialTokens);
        ArgumentNullException.ThrowIfNull(authenticatedUser);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(localStorage);
        ArgumentNullException.ThrowIfNull(ownedWorldLocationPublicationJournal);
        ArgumentNullException.ThrowIfNull(recoveryStore);
        ArgumentNullException.ThrowIfNull(lifecycleObserver);

        var normalizedBaseAddress = StewardRemoteEndpointPolicy.NormalizeApiBaseAddress(apiBaseAddress);
        var apiClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false
        })
        {
            BaseAddress = normalizedBaseAddress,
            Timeout = TimeSpan.FromSeconds(30)
        };
        var transferClient = new HttpClient(new StewardDirectTransferHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        StewardWritableReservationRegistry? reservations = null;
        StewardAccessSession? accessSession = null;
        try
        {
            var sessionClient = new StewardSessionClient(apiClient);
            var createdAccessSession = new StewardAccessSession(
                sessionClient,
                installationId,
                initialTokens);
            accessSession = createdAccessSession;

            var metadata = new StewardWorldMetadataClient(apiClient);
            var hostPresence = new StewardHostPresenceClient(apiClient);
            var worldCreation = new StewardWorldCreationClient(apiClient);
            var access = new StewardWorldAccessClient(apiClient, createdAccessSession);
            var playerPresence = new StewardWorldPlayerPresenceClient(apiClient, createdAccessSession);
            async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
                => await createdAccessSession.GetAccessTokenAsync(cancellationToken);
            var ownedWorldLocations = new StewardOwnedWorldLocationClient(
                apiClient,
                GetAccessTokenAsync);
            var ownedPrivateWorldCatalog = new StewardOwnedPrivateWorldCatalogClient(
                apiClient,
                GetAccessTokenAsync);
            var ownedWorldLocationPublication =
                new StewardOwnedWorldLocationPublicationService(
                    ownedWorldLocationPublicationJournal,
                    ownedWorldLocations,
                    installationId);
            var ownedWorldLocationCatalogReconciler =
                new StewardOwnedWorldLocationCatalogReconciler(
                    localStorage,
                    ownedWorldLocationPublicationJournal,
                    ownedWorldLocationPublication);
            var authority = new StewardAuthorityClient(apiClient);
            var abandon = new StewardReservationAbandonClient(apiClient);
            var packageDownloads = new StewardPackageDownloadClient(apiClient);
            var verifiedCache = new VerifiedPackageCache(
                Path.Combine(Path.GetFullPath(dataRoot), "package-cache"),
                transferClient);
            var packages = new StewardVerifiedPackageSource(packageDownloads, verifiedCache);
            var uploads = new StewardPackageUploadClient(apiClient, transferClient);
            var privateSnapshotTransfers = new StewardPrivateSnapshotTransferClient(
                apiClient,
                transferClient,
                verifiedCache,
                GetAccessTokenAsync);
            var privateSnapshotRevisionEvidence =
                new StewardPrivateSnapshotRevisionEvidenceClient(
                    apiClient,
                    GetAccessTokenAsync);
            var ownedWorldSnapshotPublisher = new StewardOwnedWorldSnapshotPublisher(
                localStorage,
                privateSnapshotTransfers,
                privateSnapshotRevisionEvidence);
            var initialWorldPublisher = new StewardInitialWorldPublisher(
                worldCreation,
                metadata,
                uploads,
                createdAccessSession);

            reservations = new StewardWritableReservationRegistry();
            var managedSessionGate = new ManagedWritableSessionGate();
            var coordinator = new StewardWorldSessionCoordinator(
                metadata,
                authority,
                abandon,
                createdAccessSession,
                recoveryStore,
                reservations,
                installationId,
                hostPresence: hostPresence);
            var storage = new StewardWorldStorage(
                metadata,
                packages,
                uploads,
                authority,
                createdAccessSession,
                reservations,
                recoveryStore);
            var lifecycle = new WorldLifecycleService(
                storage,
                coordinator,
                recoveryStore,
                managedSessionGate,
                lifecycleObserver);
            var coreJoin = new WorldJoinService(storage);
            var join = new CoordinatedWorldJoinService(coreJoin, playerPresence);
            var pendingSyncRecovery = new StewardPendingSyncRecoveryService(
                storage,
                coordinator,
                abandon,
                createdAccessSession,
                reservations,
                recoveryStore,
                managedSessionGate);

            return new StewardDesktopRemoteRuntime(
                apiClient,
                transferClient,
                reservations,
                createdAccessSession,
                metadata,
                hostPresence,
                coordinator,
                ownedWorldLocationPublication,
                ownedWorldLocationCatalogReconciler,
                ownedWorldSnapshotPublisher,
                ownedPrivateWorldCatalog,
                storage,
                lifecycle,
                join,
                pendingSyncRecovery,
                initialWorldPublisher,
                access,
                playerPresence,
                ownedWorldLocations,
                authenticatedUser);
        }
        catch
        {
            reservations?.Dispose();
            accessSession?.Dispose();
            transferClient.Dispose();
            apiClient.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reservations.Dispose();
        _accessSession.Dispose();
        _transferClient.Dispose();
        _apiClient.Dispose();
    }
}
