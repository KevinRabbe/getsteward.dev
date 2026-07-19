using System.Diagnostics;
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
        GameAdapterCapabilities.AutomaticClientJoin |
        GameAdapterCapabilities.ExactGameVersion;

    public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(FactorioInstallationDiscovery.Discover());
    }

    public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(FactorioWorldOperations.DiscoverWorlds(installation));
    }

    public Task<EnvironmentManifest> InspectEnvironmentAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
        => FactorioEnvironmentInspector.InspectAsync(installation, cancellationToken);

    public Task<CapturedState> CaptureDetectedWorldAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
        => FactorioWorldOperations.CaptureDetectedWorldAsync(world, cancellationToken);

    public Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
        => FactorioWorldOperations.PrepareEnvironmentAsync(
            installation,
            requiredEnvironment,
            cancellationToken);

    public Task<CapturedState> CaptureStateAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
        => FactorioWorldOperations.CaptureStateAsync(world, cancellationToken);

    public Task RestoreStateAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken = default)
        => FactorioWorldOperations.RestoreStateAsync(world, state, cancellationToken);

    public Task<GameSessionHandle> LaunchHostAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
        => FactorioWorldOperations.LaunchHostAsync(world, cancellationToken);

    public Task<GameSessionHandle> LaunchClientAsync(
        PreparedWorld world,
        HostConnection host,
        CancellationToken cancellationToken = default)
        => FactorioWorldOperations.LaunchClientAsync(world, host, cancellationToken);

    public async Task WaitForSessionEndAsync(
        GameSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.GetProcessById(session.ProcessId);
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (ArgumentException)
        {
            // The process already exited before we started observing it.
        }
    }
}
