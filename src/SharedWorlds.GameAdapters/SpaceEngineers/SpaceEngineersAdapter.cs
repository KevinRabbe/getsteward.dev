using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.SpaceEngineers;

public sealed class SpaceEngineersAdapter : IGameAdapter
{
    public string Id => "space-engineers";
    public string DisplayName => "Space Engineers";
    public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.ExactGameVersion;

    public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SpaceEngineersInstallationDiscovery.Discover());
    }

    public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SpaceEngineersWorldDiscovery.Discover(installation));
    }

    public Task<EnvironmentManifest> InspectEnvironmentAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SpaceEngineersEnvironment.Inspect(installation, world));
    }

    public Task<EnvironmentVerificationReport> VerifyEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SpaceEngineersEnvironment.Verify(installation, requiredEnvironment));
    }

    public Task<CapturedState> CaptureDetectedWorldAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
        => SpaceEngineersWorldState.CaptureDetectedWorldAsync(world, cancellationToken);

    public Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SpaceEngineersWorldState.PrepareEnvironment(installation, requiredEnvironment));
    }

    public Task<CapturedState> CaptureStateAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
        => SpaceEngineersWorldState.CapturePreparedWorldAsync(world, cancellationToken);

    public Task RestoreStateAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken = default)
        => SpaceEngineersWorldState.RestorePreparedWorldAsync(world, state, cancellationToken);

    public Task FinalizePreparedWorldAsync(
        PreparedWorld world,
        PreparedWorldDisposition disposition,
        CancellationToken cancellationToken = default)
        => SpaceEngineersWorldState.FinalizePreparedWorldAsync(world, disposition, cancellationToken);
}
