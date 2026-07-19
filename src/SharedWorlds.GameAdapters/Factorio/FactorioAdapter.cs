using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Factorio;

public sealed class FactorioAdapter : IGameAdapter
{
    public string Id => "factorio";
    public string DisplayName => "Factorio";
    public GameAdapterCapabilities Capabilities =>
        GameAdapterCapabilities.Mods |
        GameAdapterCapabilities.AutomaticHostLaunch |
        GameAdapterCapabilities.ExactModVersions |
        GameAdapterCapabilities.EnvironmentIsolation;

    public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<GameInstallation>>([]);

    public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(GameInstallation installation, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DetectedWorld>>([]);

    public Task<EnvironmentManifest> InspectEnvironmentAsync(GameInstallation installation, DetectedWorld world, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<PreparedWorld> PrepareEnvironmentAsync(GameInstallation installation, EnvironmentManifest requiredEnvironment, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<CapturedState> CaptureStateAsync(PreparedWorld world, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task RestoreStateAsync(PreparedWorld world, StatePackage state, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<GameSessionHandle> LaunchHostAsync(PreparedWorld world, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<GameSessionHandle> LaunchClientAsync(PreparedWorld world, HostConnection host, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
