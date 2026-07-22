using System.IO;
using System.Net.Http;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Desktop;

/// <summary>
/// Owns the authenticated shared-World runtime used by the Windows desktop after Steam identity has
/// already been verified. Steam ticket acquisition is intentionally outside this composition root;
/// once authenticated, all shared World metadata, transfer, authority, commit, recovery, and flat
/// access-management traffic flows through the production remote Infrastructure implementations.
/// </summary>
internal sealed class StewardDesktopRemoteRuntime : IDisposable
{
    private readonly HttpClient _apiClient;
    private readonly HttpClient _transferClient;
    private readonly StewardWritableReservationRegistry _reservations;
    private readonly StewardAccessSession _accessSession;
    private readonly StewardWorldMetadataClient _metadata;
    private bool _disposed;

    private StewardDesktopRemoteRuntime(
        HttpClient apiClient,
        HttpClient transferClient,
        StewardWritableReservationRegistry reservations,
        StewardAccessSession accessSession,
        StewardWorldMetadataClient metadata,
        StewardWorldStorage storage,
        WorldLifecycleService lifecycle,
        StewardPendingSyncRecoveryService pendingSyncRecovery,
        StewardInitialWorldPublisher initialWorldPublisher,
        StewardWorldAccessClient access,
        UserIdentity user)
    {
        _apiClient = apiClient;
        _transferClient = transferClient;
        _reservations = reservations;
        _accessSession = accessSession;
        _metadata = metadata;
        Storage = storage;
        Lifecycle = lifecycle;
        PendingSyncRecovery = pendingSyncRecovery;
        InitialWorldPublisher = initialWorldPublisher;
        Access = access;
        User = user;
    }

    public StewardWorldStorage Storage { get; }
    public WorldLifecycleService Lifecycle { get; }
    public StewardPendingSyncRecoveryService PendingSyncRecovery { get; }
    public StewardInitialWorldPublisher InitialWorldPublisher { get; }
    public StewardWorldAccessClient Access { get; }
    public UserIdentity User { get; }

    public async Task<StewardRemoteWorldMetadata?> GetWorldMetadataAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        var accessToken = await _accessSession.GetAccessTokenAsync(cancellationToken);
        return await _metadata.GetWorldAsync(worldId, accessToken, cancellationToken);
    }

    public static StewardDesktopRemoteRuntime Create(
        Uri apiBaseAddress,
        string installationId,
        StewardRemoteSessionTokens initialTokens,
        UserIdentity authenticatedUser,
        string dataRoot,
        IWorkspaceRecoveryStore recoveryStore,
        IWorldLifecycleObserver lifecycleObserver)
    {
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        ArgumentNullException.ThrowIfNull(initialTokens);
        ArgumentNullException.ThrowIfNull(authenticatedUser);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(recoveryStore);
        ArgumentNullException.ThrowIfNull(lifecycleObserver);

        var normalizedBaseAddress = NormalizeApiBaseAddress(apiBaseAddress);
        var apiClient = new HttpClient
        {
            BaseAddress = normalizedBaseAddress,
            Timeout = TimeSpan.FromSeconds(30)
        };
        var transferClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        StewardWritableReservationRegistry? reservations = null;
        StewardAccessSession? accessSession = null;
        try
        {
            var sessionClient = new StewardSessionClient(apiClient);
            accessSession = new StewardAccessSession(
                sessionClient,
                installationId,
                initialTokens);

            var metadata = new StewardWorldMetadataClient(apiClient);
            var worldCreation = new StewardWorldCreationClient(apiClient);
            var access = new StewardWorldAccessClient(apiClient, accessSession);
            var authority = new StewardAuthorityClient(apiClient);
            var abandon = new StewardReservationAbandonClient(apiClient);
            var packageDownloads = new StewardPackageDownloadClient(apiClient);
            var verifiedCache = new VerifiedPackageCache(
                Path.Combine(Path.GetFullPath(dataRoot), "package-cache"),
                transferClient);
            var packages = new StewardVerifiedPackageSource(packageDownloads, verifiedCache);
            var uploads = new StewardPackageUploadClient(apiClient, transferClient);
            var initialWorldPublisher = new StewardInitialWorldPublisher(
                worldCreation,
                metadata,
                uploads,
                accessSession);

            reservations = new StewardWritableReservationRegistry();
            var managedSessionGate = new ManagedWritableSessionGate();
            var coordinator = new StewardWorldSessionCoordinator(
                metadata,
                authority,
                abandon,
                accessSession,
                recoveryStore,
                reservations,
                installationId);
            var storage = new StewardWorldStorage(
                metadata,
                packages,
                uploads,
                authority,
                accessSession,
                reservations,
                recoveryStore);
            var lifecycle = new WorldLifecycleService(
                storage,
                coordinator,
                recoveryStore,
                managedSessionGate,
                lifecycleObserver);
            var pendingSyncRecovery = new StewardPendingSyncRecoveryService(
                storage,
                coordinator,
                abandon,
                accessSession,
                reservations,
                recoveryStore,
                managedSessionGate);

            return new StewardDesktopRemoteRuntime(
                apiClient,
                transferClient,
                reservations,
                accessSession,
                metadata,
                storage,
                lifecycle,
                pendingSyncRecovery,
                initialWorldPublisher,
                access,
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

    private static Uri NormalizeApiBaseAddress(Uri apiBaseAddress)
    {
        if (!apiBaseAddress.IsAbsoluteUri ||
            apiBaseAddress.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                "Steward API base address must be an absolute HTTP or HTTPS URI.",
                nameof(apiBaseAddress));
        }

        var absolute = apiBaseAddress.AbsoluteUri;
        return absolute.EndsWith("/", StringComparison.Ordinal)
            ? apiBaseAddress
            : new Uri(absolute + '/', UriKind.Absolute);
    }
}
