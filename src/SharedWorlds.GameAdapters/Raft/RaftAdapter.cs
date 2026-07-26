using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Raft;

public sealed class RaftAdapter : IGameAdapter
{
    public string Id => "raft";
    public string DisplayName => "Raft";
    public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.ExactGameVersion;

    public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RaftInstallationDiscovery.Discover());
    }

    public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(GameInstallation installation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RaftWorldDiscovery.Discover(installation));
    }

    public Task<EnvironmentManifest> InspectEnvironmentAsync(GameInstallation installation, DetectedWorld world, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RaftEnvironment.Inspect(installation));
    }

    public Task<EnvironmentVerificationReport> VerifyEnvironmentAsync(GameInstallation installation, EnvironmentManifest requiredEnvironment, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RaftEnvironment.Verify(installation, requiredEnvironment));
    }

    public Task<CapturedState> CaptureDetectedWorldAsync(GameInstallation installation, DetectedWorld world, CancellationToken cancellationToken = default)
        => RaftWorldState.CaptureDetectedWorldAsync(world, cancellationToken);

    public Task<PreparedWorld> PrepareEnvironmentAsync(GameInstallation installation, EnvironmentManifest requiredEnvironment, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RaftWorldState.PrepareEnvironment(installation, requiredEnvironment));
    }

    public Task<CapturedState> CaptureStateAsync(PreparedWorld world, CancellationToken cancellationToken = default)
        => RaftWorldState.CapturePreparedWorldAsync(world, cancellationToken);

    public Task RestoreStateAsync(PreparedWorld world, StatePackage state, CancellationToken cancellationToken = default)
        => RaftWorldState.RestorePreparedWorldAsync(world, state, cancellationToken);

    public Task FinalizePreparedWorldAsync(PreparedWorld world, PreparedWorldDisposition disposition, CancellationToken cancellationToken = default)
        => RaftWorldState.FinalizePreparedWorldAsync(world, disposition, cancellationToken);
}
