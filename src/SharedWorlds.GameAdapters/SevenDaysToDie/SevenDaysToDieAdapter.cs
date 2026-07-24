using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.SevenDaysToDie;

public sealed class SevenDaysToDieAdapter : IGameAdapter
{
    public string Id => "7-days-to-die";
    public string DisplayName => "7 Days to Die";
    public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.None;

    public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SevenDaysToDieInstallationDiscovery.Discover());
    }

    public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(GameInstallation installation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SevenDaysToDieWorldDiscovery.Discover(installation));
    }

    public Task<EnvironmentManifest> InspectEnvironmentAsync(GameInstallation installation, DetectedWorld world, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<CapturedState> CaptureDetectedWorldAsync(GameInstallation installation, DetectedWorld world, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<PreparedWorld> PrepareEnvironmentAsync(GameInstallation installation, EnvironmentManifest requiredEnvironment, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<CapturedState> CaptureStateAsync(PreparedWorld world, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task RestoreStateAsync(PreparedWorld world, StatePackage state, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<GameSessionHandle> LaunchLocalAsync(PreparedWorld world, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<GameSessionHandle> LaunchHostAsync(PreparedWorld world, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<GameSessionHandle> LaunchClientAsync(PreparedWorld world, HostConnection host, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task WaitForSessionEndAsync(GameSessionHandle session, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task FinalizePreparedWorldAsync(
        PreparedWorld world,
        PreparedWorldDisposition disposition,
        CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}
