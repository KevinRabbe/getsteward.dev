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
    private readonly HashSet<WorldId> _remoteWorldIds = [];
    private readonly HashSet<WorldId> _remoteIncompleteWorldIds = [];
    private StewardDesktopRemoteRuntime? _remoteRuntime;
    private Exception? _lastRemoteWorldLoadError;

    /// <summary>
    /// Connects an already-authenticated Steward session to the real shared-World runtime. The caller
    /// supplies the backend-verified identity and initial Steward credentials; this method owns the
    /// resulting remote runtime until it is replaced or the desktop window closes.
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
            _workspaceRecoveryStore,
            CreateDesktopLifecycleObserver());

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

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            next.Dispose();
            throw;
        }

        var previous = _remoteRuntime;
        _remoteRuntime = next;
        _lastRemoteWorldLoadError = null;
        previous?.Dispose();

        await RefreshUnifiedWorldsAsync(
            _selectedWorld?.Id,
            preserveStatus: false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<IReadOnlyList<World>> ListDesktopWorldsAsync(
        CancellationToken cancellationToken = default)
    {
        var localWorlds = await _storage.ListWorldsAsync(cancellationToken);
        var localById = localWorlds.ToDictionary(world => world.Id);
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
            // A shared-service outage or invalid remote response must not make private local Worlds
            // unusable. A local shadow already marked Shared remains non-writable through the normal
            // authoritative-runtime guard; it never becomes LocalOnly merely because the backend is down.
            _lastRemoteWorldLoadError = exception;
            return localWorlds;
        }

        foreach (var remoteWorld in remoteWorlds)
        {
            if (!localById.ContainsKey(remoteWorld.Id))
            {
                // Normal shared/invited Worlds have no local shadow. Backend membership is sufficient
                // to list them; exact metadata and environment gates still fail closed before play.
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
                    // The backend World identity may have been created immediately before a crash or
                    // transfer failure. Keep the local shadow visible only as a locked sharing journal
                    // so Share World can resume the same immutable publication IDs.
                    _remoteIncompleteWorldIds.Add(remoteWorld.Id);
                }
            }
            catch (Exception exception) when (IsRemoteAvailabilityFailure(exception))
            {
                _lastRemoteWorldLoadError ??= exception;
                _remoteIncompleteWorldIds.Add(remoteWorld.Id);
            }
        }

        // A complete backend copy with the same World ID is authoritative. An incomplete backend copy
        // deliberately does NOT hide the local shadow, because that shadow is the immutable source for
        // resuming initial publication. The shadow is already marked Shared before remote side effects,
        // so it cannot accidentally acquire local writable authority while publication is incomplete.
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
        return world.SharingMode == WorldSharingMode.LocalOnly ||
               (_remoteRuntime is not null && _remoteWorldIds.Contains(world.Id));
    }

    private IWorldStorage GetStorageForWorld(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_remoteWorldIds.Contains(world.Id))
        {
            return _remoteRuntime?.Storage
                ?? throw new InvalidOperationException(
                    "The selected shared World no longer has an authenticated Steward runtime.");
        }

        return _storage;
    }

    private WorldLifecycleService GetLifecycleForWorld(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_remoteWorldIds.Contains(world.Id))
        {
            return _remoteRuntime?.Lifecycle
                ?? throw new InvalidOperationException(
                    "The selected shared World no longer has an authenticated Steward runtime.");
        }

        if (world.SharingMode == WorldSharingMode.Shared)
        {
            throw new InvalidOperationException(
                "A shared World can never fall back to local writable authority. Reconnect the authenticated Steward backend before play.");
        }

        return _lifecycle;
    }

    private UserIdentity GetUserForWorld(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (_remoteWorldIds.Contains(world.Id))
        {
            return _remoteRuntime?.User
                ?? throw new InvalidOperationException(
                    "The selected shared World no longer has an authenticated Steward identity.");
        }

        if (world.SharingMode == WorldSharingMode.Shared)
        {
            throw new InvalidOperationException(
                "A shared World requires an authenticated Steward identity before writable play.");
        }

        return GetLocalUser();
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
        _remoteRuntime?.Dispose();
        _remoteRuntime = null;
    }

    private static bool IsRemoteAvailabilityFailure(Exception exception)
        => exception is StewardRemoteApiException or
            HttpRequestException or
            IOException or
            TimeoutException or
            TaskCanceledException;
}
