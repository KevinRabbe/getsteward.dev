using System.Diagnostics;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Palworld;

public sealed class PalworldAdapter : IGameAdapter
{
    public string Id => "palworld";
    public string DisplayName => "Palworld";

    // The dedicated-host bootstrap, native state capture, and canonical restore path are now wired,
    // but capabilities stay conservative until the full lifecycle is runtime-validated end to end.
    public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.None;

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
        return Task.FromResult(PalworldDedicatedServerHosting.PrepareDetectedWorld(installation, world));
    }

    public Task<EnvironmentManifest> InspectEnvironmentAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PalworldDedicatedServerHosting.InspectEnvironment(world));
    }

    public async Task<CapturedState> CaptureDetectedWorldAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var captured = await PalworldWorldState.CaptureDetectedWorldAsync(world, cancellationToken);
        return captured with { DeletePackageAfterStore = true };
    }

    public Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            PalworldDedicatedServerHosting.PrepareEnvironment(installation, requiredEnvironment));
    }

    public async Task<CapturedState> CaptureStateAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
    {
        var captured = await PalworldWorldState.CapturePreparedWorldAsync(world, cancellationToken);
        return captured with { DeletePackageAfterStore = true };
    }

    public Task RestoreStateAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken = default)
        => PalworldWorldState.RestorePreparedWorldAsync(world, state, cancellationToken);

    public Task<GameSessionHandle> LaunchLocalAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<GameSessionHandle> LaunchHostAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PalworldDedicatedServerHosting.Launch(world));
    }

    public Task<GameSessionHandle> LaunchClientAsync(
        PreparedWorld world,
        HostConnection host,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public async Task WaitForSessionEndAsync(
        GameSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Process process;
        try
        {
            process = Process.GetProcessById(session.ProcessId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken);
        }
    }

    public Task FinalizePreparedWorldAsync(
        PreparedWorld world,
        PreparedWorldDisposition disposition,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
