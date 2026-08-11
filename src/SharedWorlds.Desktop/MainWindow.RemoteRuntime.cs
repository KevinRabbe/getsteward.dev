using System.IO;
using System.Net.Http;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private readonly HashSet<WorldId> _peerWorldIds = [];
    private readonly HashSet<WorldId> _remoteWorldIds = [];
    private readonly HashSet<WorldId> _remoteIncompleteWorldIds = [];
    private StewardDesktopRemoteRuntime? _remoteRuntime;
    private Exception? _lastRemoteWorldLoadError;

    /// <summary>
    /// Connects an already-authenticated Steward session to the legacy shared-World runtime. The caller
    /// supplies the backend-verified identity and initial Steward credentials; this method owns the
    /// resulting remote runtime until it is replaced or the desktop window closes. Local Worlds that
    /// already carry persistent peer authority are never routed back through this runtime.
    /// </summary>
    internal async Task SetAuthenticatedRemoteRuntimeAsync(
        Uri apiBaseAddress,
        StewardRemoteSessionTokens initialTokens,
        UserIdentity authenticatedUser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apiBaseAddress);
        ArgumentNullException.ThrowIfNull(initialTokens);
        ArgumentNullException.ThrowIfNull(authenticatedUser);

        var remoteRoot = Path.Combine(
            GetLocalDataRoot(),
            "SharedWorlds",
            "remote");
        var next = StewardDesktopRemoteRuntime.Create(
            apiBaseAddress,
            _deviceSettings.InstallationId,
            initialTokens,
            authenticatedUser,
            remoteRoot,
            _storage,
            _ownedWorldLocationPublicationJournal,
            _workspaceRecoveryStore,
            CreateDesktopLifecycleObserver());

        StewardDesktopRemoteRuntime? previous = null;
        var publicationGateHeld = false;
        try
        {
            var registration = await next.OwnedWorldLocations.RegisterCurrentInstallationAsync(
                Environment.MachineName,
                cancellationToken);
            if (registration.IsConflict ||
                !string.Equals(
                    registration.Code,
                    "InstallationRegistered",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Steward did not confirm this Safe World installation registration.");
            }

            // Candidate replay and active-runtime replay share this one gate. Once acquired, no old
            // runtime can touch the publication journal again before the candidate becomes active.
            await _ownedWorldLocationPublicationGate.WaitAsync(cancellationToken);
            publicationGateHeld = true;
            await next.ReconcileAndReplayOwnedWorldLocationsAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            previous = Interlocked.Exchange(ref _remoteRuntime, next);
            _lastRemoteWorldLoadError = null;
        }
        catch
        {
            next.Dispose();
            throw;
        }
        finally
        {
            if (publicationGateHeld)
            {
                _ownedWorldLocationPublicationGate.Release();
            }
        }

        previous?.Dispose();

        // A local mutation may have completed while the candidate was reconciling. Queue one bounded
        // pass after the swap so that mutation is observed through the newly active session.
        _ownedWorldLocationPublicationTrigger.Request();

        await RefreshUnifiedWorldsAsync(
            _selectedWorld?.Id,
            preserveStatus: false);
        await RefreshOwnedPrivateWorldCatalogAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<IReadOnlyList<World>> ListDesktopWorldsAsync(
        CancellationToken cancellationToken = default)
    {
        var localWorlds = await _storage.ListWorldsAsync(cancellationToken);
        var localById = localWorlds.ToDictionary(world => world.Id);
        _peerWorldIds.Clear();
        foreach (var localWorld in localWorlds)
        {
            if (localWorld.SharingMode == WorldSharingMode.Shared &&
                localWorld.PeerAuthority is not null)
            {
                // Classification is intentionally based on a local replica. A remote-only invited
                // World may eventually contain peer metadata too, but it is not usable through the
                // embedded peer runtime until bootstrap has installed its canonical bytes locally.
                _peerWorldIds.Add(localWorld.Id);
            }
        }

        _remoteWorldIds.Clear();
        _remoteIncompleteWorldIds.Clear();
        _lastRemoteWorldLoadError = null;

        var remote = _remoteRuntime;
        if (remote is null)
        {
            return localWorlds;
        }

        IReadOnlyList<World> remoteWorlds;
        try
        {
            remoteWorlds = await remote.Storage.ListWorldsAsync(cancellationToken);
        }
        catch (Exception exception) when (IsRemoteAvailabilityFailure(exception))
        {
            // A legacy shared-service outage or invalid response must not make local Worlds unusable.
            // Shared peer Worlds remain fenced by their embedded runtime classification and never
            // become local writable fallbacks because the service is unavailable.
            _lastRemoteWorldLoadError = exception;
            return localWorlds;
        }

        foreach (var remoteWorld in remoteWorlds)
        {
            if (_peerWorldIds.Contains(remoteWorld.Id))
            {
                // Persistent peer authority is an irreversible product cutover for this local replica.
                // Never hide it behind a same-ID legacy backend copy or route it back to remote authority.
                continue;
            }

            if (!localById.ContainsKey(remoteWorld.Id))
            {
                // Transitional remote-only shared/invited Worlds have no local peer replica yet.
                _remoteWorldIds.Add(remoteWorld.Id);
                continue;
            }

            try
            {
                if (await IsRemoteInitialSnapshotCompleteAsync(remoteWorld, cancellationToken))
                {
                    _remoteWorldIds.Add(remoteWorld.Id);
                }
                else
                {
                    // The legacy backend World identity may have been created immediately before a
                    // crash or transfer failure. Keep the local shadow visible only as a locked
                    // sharing journal so publication can resume the same immutable IDs.
                    _remoteIncompleteWorldIds.Add(remoteWorld.Id);
                }
            }
            catch (Exception exception) when (IsRemoteAvailabilityFailure(exception))
            {
                _lastRemoteWorldLoadError ??= exception;
                _remoteIncompleteWorldIds.Add(remoteWorld.Id);
            }
        }

        // A complete legacy remote copy may still hide an old non-peer local shadow during migration.
        // A local World with persistent peer authority is never inserted into _remoteWorldIds above and
        // therefore always remains the visible canonical entry on this installation.
        return localWorlds
            .Where(world => !_remoteWorldIds.Contains(world.Id))
            .Concat(remoteWorlds.Where(world => _remoteWorldIds.Contains(world.Id)))
            .ToArray();
    }

    private async Task<bool> IsRemoteInitialSnapshotCompleteAsync(
        World remoteWorld,
        CancellationToken cancellationToken)
    {
        var remote = _remoteRuntime
            ?? throw new InvalidOperationException(
                "Authenticated Steward runtime disappeared while checking shared publication state.");
        var stateId = remoteWorld.CurrentStateRevisionId;
        var environmentId = remoteWorld.CurrentEnvironmentRevisionId;
        if (stateId is null || environmentId is null)
        {
            return false;
        }

        var state = await remote.Storage.LoadStateRevisionAsync(
            remoteWorld.Id,
            stateId.Value,
            cancellationToken);
        if (state is null)
        {
            return false;
        }

        var environment = await remote.Storage.LoadEnvironmentRevisionAsync(
            remoteWorld.Id,
            environmentId.Value,
            cancellationToken);
        return environment is not null;
    }

    private bool HasAuthoritativeRuntimeForWorld(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.SharingMode == WorldSharingMode.LocalOnly)
        {
            return true;
        }

        if (_peerWorldIds.Contains(world.Id))
        {
            return _peerRuntime is not null;
        }

        return _remoteRuntime is not null && _remoteWorldIds.Contains(world.Id);
    }

    private IWorldStorage GetStorageForWorld(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_peerWorldIds.Contains(world.Id))
        {
            return RequirePeerRuntime(world).Storage;
        }

        if (_remoteWorldIds.Contains(world.Id))
        {
            return _remoteRuntime?.Storage
                ?? throw new InvalidOperationException(
                    "The selected legacy shared World no longer has an authenticated Steward runtime.");
        }

        return _storage;
    }

    private WorldLifecycleService GetLifecycleForWorld(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_peerWorldIds.Contains(world.Id))
        {
            return RequirePeerRuntime(world).Lifecycle;
        }

        if (_remoteWorldIds.Contains(world.Id))
        {
            return _remoteRuntime?.Lifecycle
                ?? throw new InvalidOperationException(
                    "The selected legacy shared World no longer has an authenticated Steward runtime.");
        }

        if (world.SharingMode == WorldSharingMode.Shared)
        {
            throw new InvalidOperationException(
                "A shared World can never fall back to unfenced local writable authority. Reconnect the required Steward authority runtime before play.");
        }

        return _lifecycle;
    }

    private UserIdentity GetUserForWorld(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_peerWorldIds.Contains(world.Id))
        {
            return RequirePeerRuntime(world).User;
        }

        if (_remoteWorldIds.Contains(world.Id))
        {
            return _remoteRuntime?.User
                ?? throw new InvalidOperationException(
                    "The selected legacy shared World no longer has an authenticated Steward identity.");
        }

        if (world.SharingMode == WorldSharingMode.Shared)
        {
            throw new InvalidOperationException(
                "A shared World requires an authenticated or persistent peer identity before writable play.");
        }

        return GetLocalUser();
    }

    private IGameAdapter GetManagedHostAdapterForWorld(
        World world,
        IGameAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(adapter);
        return _peerWorldIds.Contains(world.Id)
            ? RequirePeerRuntime(world).CoordinateManagedHost(world.Id, adapter)
            : adapter;
    }

    private StewardDesktopPeerRuntime RequirePeerRuntime(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return _peerRuntime
            ?? throw new InvalidOperationException(
                _peerRuntimeProblem is null
                    ? $"Shared World '{world.Name}' uses persistent peer authority, but the embedded Steam peer runtime is unavailable on this launch."
                    : $"Shared World '{world.Name}' uses persistent peer authority, but the embedded Steam peer runtime is unavailable: {_peerRuntimeProblem}");
    }

    private async Task<EnvironmentRevision?> LoadEnvironmentRevisionForWorldAsync(
        World world,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetStorageForWorld(world).LoadEnvironmentRevisionAsync(
                world.Id,
                revisionId,
                cancellationToken);
        }
        catch (Exception exception) when (
            _remoteWorldIds.Contains(world.Id) &&
            IsRemoteAvailabilityFailure(exception))
        {
            _lastRemoteWorldLoadError ??= exception;
            return null;
        }
    }

    private void DisposeRemoteRuntime()
    {
        _remoteWorldIds.Clear();
        _remoteIncompleteWorldIds.Clear();
        _lastRemoteWorldLoadError = null;
        Interlocked.Exchange(ref _remoteRuntime, null)?.Dispose();
    }

    private static bool IsRemoteAvailabilityFailure(Exception exception)
        => exception is StewardRemoteApiException or
            HttpRequestException or
            IOException or
            TimeoutException or
            TaskCanceledException;
}
