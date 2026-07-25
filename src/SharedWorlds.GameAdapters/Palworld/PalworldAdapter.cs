using System.Collections.Concurrent;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Palworld;

public sealed partial class PalworldAdapter : IGameAdapter
{
    private readonly ConcurrentDictionary<int, PalworldManagedHostSession> _managedHosts = new();

    public string Id => "palworld";
    public string DisplayName => "Palworld";

    public GameAdapterCapabilities Capabilities =>
        GameAdapterCapabilities.AutomaticHostLaunch |
        GameAdapterCapabilities.AutomaticHostStop |
        GameAdapterCapabilities.ExactGameVersion;

    public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PalworldInstallationDiscovery.Discover());
    }

    public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PalworldSaveDiscovery.Discover(installation));
    }

    /// <summary>
    /// Bootstraps a detected native Palworld world into the installed dedicated server using the
    /// empirically validated mechanism: copy the native world directory, then return a prepared
    /// world that can be passed to LaunchHostAsync. Player identity migration is deliberately
    /// outside this path.
    /// </summary>
    public Task<PreparedWorld> PrepareDetectedWorldForHostingAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PalworldDedicatedRuntimeInputSafety.ValidatePreparationInputs(installation);
        return Task.FromResult(PalworldDedicatedServerHosting.PrepareDetectedWorld(installation, world));
    }

    public Task<EnvironmentManifest> InspectEnvironmentAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        PalworldDedicatedRuntimeInputSafety.ValidateEnvironmentInspection(installation);
        return Task.FromResult(PalworldDedicatedServerHosting.InspectEnvironment(installation, world));
    }

    public Task<CapturedState> CaptureDetectedWorldAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        return PalworldWorldState.CaptureDetectedWorldAsync(world, cancellationToken);
    }

    public Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PalworldDedicatedRuntimeInputSafety.ValidatePreparationInputs(installation);
        return Task.FromResult(
            PalworldDedicatedServerHosting.PrepareEnvironment(installation, requiredEnvironment));
    }

    public Task<CapturedState> CaptureStateAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        PalworldWorkspaceOwnership.RequireOwned(world);
        return PalworldWorldState.CapturePreparedWorldAsync(world, cancellationToken);
    }

    public Task RestoreStateAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(state);
        PalworldWorkspaceOwnership.RequireOwned(world);
        PalworldStatePackagePreflight.Validate(
            state.Path,
            world.WorkingDirectory,
            cancellationToken);
        return PalworldWorldState.RestorePreparedWorldAsync(world, state, cancellationToken);
    }

    public async Task<GameSessionHandle> LaunchHostAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
    {
        PalworldWorkspaceOwnership.RequireOwned(world);
        PalworldDedicatedRuntimeInputSafety.ValidateManagedHostInputs(world);
        var managed = await PalworldManagedHostSession.StartAsync(world, cancellationToken);
        var handle = managed.Handle;
        if (!_managedHosts.TryAdd(handle.ProcessId, managed))
        {
            try
            {
                await managed.RequestStopAsync(CancellationToken.None);
            }
            finally
            {
                managed.Dispose();
            }

            throw new InvalidOperationException(
                $"Palworld process {handle.ProcessId} is already registered as a managed host.");
        }

        return handle;
    }

    public async Task RequestHostStopAsync(
        GameSessionHandle handle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_managedHosts.TryGetValue(handle.ProcessId, out var managed))
        {
            throw new InvalidOperationException(
                $"Palworld process {handle.ProcessId} is not registered as a Steward-managed host.");
        }

        await managed.RequestStopAsync(cancellationToken);
    }

    public async Task WaitForHostExitAsync(
        GameSessionHandle handle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_managedHosts.TryGetValue(handle.ProcessId, out var managed))
        {
            throw new InvalidOperationException(
                $"Palworld process {handle.ProcessId} is not registered as a Steward-managed host.");
        }

        try
        {
            await managed.WaitForCompletionAsync().WaitAsync(cancellationToken);
        }
        finally
        {
            if (managed.WaitForCompletionAsync().IsCompleted &&
                _managedHosts.TryRemove(handle.ProcessId, out var removed))
            {
                removed.Dispose();
            }
        }
    }

    public Task FinalizePreparedWorldAsync(
        PreparedWorld world,
        PreparedWorldDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        cancellationToken.ThrowIfCancellationRequested();
        PalworldWorkspaceOwnership.RequireOwned(world);

        // Palworld's proven dedicated-host path operates directly in PalServer's native save tree.
        // There is no disposable Steward-owned World workspace to delete here.
        return Task.CompletedTask;
    }
}
