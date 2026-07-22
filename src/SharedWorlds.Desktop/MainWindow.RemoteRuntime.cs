using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private readonly HashSet<WorldId> _remoteWorldIds = [];
    private StewardDesktopRemoteRuntime? _remoteRuntime;
    private Exception? _lastRemoteWorldLoadError;

    /// <summary>
    /// Connects the already-authenticated Steam session to the real shared-World runtime. The caller
    /// that acquires the Steam authentication ticket supplies the verified identity and initial
    /// Steward credentials; this method owns the resulting remote runtime until it is replaced or
    /// the desktop window closes.
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
        _remoteWorldIds.Clear();
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
            // A shared-service outage must not make private local Worlds unusable. Keep the local
            // library available and surface the remote failure in the status line instead.
            _lastRemoteWorldLoadError = exception;
            return localWorlds;
        }

        foreach (var world in remoteWorlds)
        {
            _remoteWorldIds.Add(world.Id);
        }

        // If the same World ID exists in the old local store and in Steward, the backend copy is the
        // canonical shared World. Do not display two competing heads; leave the local bytes untouched.
        return localWorlds
            .Where(world => !_remoteWorldIds.Contains(world.Id))
            .Concat(remoteWorlds)
            .ToArray();
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

        return GetLocalUser();
    }

    private async Task<EnvironmentRevision?> LoadEnvironmentRevisionForWorldAsync(
        World world,
        RevisionId revisionId,
        CancellationToken cancellationToken = default)
        => await GetStorageForWorld(world).LoadEnvironmentRevisionAsync(
            world.Id,
            revisionId,
            cancellationToken);

    private void DisposeRemoteRuntime()
    {
        _remoteWorldIds.Clear();
        _remoteRuntime?.Dispose();
        _remoteRuntime = null;
    }

    private static bool IsRemoteAvailabilityFailure(Exception exception)
        => exception is StewardSessionExpiredException or
            StewardRemoteApiException or
            HttpRequestException or
            IOException or
            TimeoutException;
}
