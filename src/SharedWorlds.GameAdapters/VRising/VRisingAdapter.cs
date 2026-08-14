using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.VRising;

public sealed class VRisingAdapter : IGameAdapter, IPreparedWorldRecoveryPlanner
{
    public string Id => "v-rising";
    public string DisplayName => "V Rising";
    public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.ExactGameVersion;

    public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(VRisingInstallationDiscovery.Discover());
    }

    public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(VRisingWorldDiscovery.Discover(installation));
    }

    public Task<EnvironmentManifest> InspectEnvironmentAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(VRisingEnvironment.Inspect(installation));
    }

    public Task<EnvironmentVerificationReport> VerifyEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(VRisingEnvironment.Verify(installation, requiredEnvironment));
    }

    public Task<CapturedState> CaptureDetectedWorldAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
        => VRisingWorldState.CaptureDetectedWorldAsync(world, cancellationToken);

    public Task<PreparedWorldRecoveryLocation> PlanPreparedWorldRecoveryAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        PreparedWorldPreparationContext preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(requiredEnvironment);
        ArgumentNullException.ThrowIfNull(preparation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(requiredEnvironment.AdapterId, Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Environment belongs to adapter '{requiredEnvironment.AdapterId}', not V Rising.",
                nameof(requiredEnvironment));
        }

        return Task.FromResult(PreparedWorldRecoveryLocation.Managed());
    }

    public Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        PreparedWorldPreparationContext preparation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(VRisingWorldState.PrepareEnvironment(
            installation,
            requiredEnvironment,
            preparation));
    }

    public Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(VRisingWorldState.PrepareEnvironment(installation, requiredEnvironment));
    }

    public Task<CapturedState> CaptureStateAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
        => VRisingWorldState.CapturePreparedWorldAsync(world, cancellationToken);

    public Task RestoreStateAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken = default)
        => VRisingWorldState.RestorePreparedWorldAsync(world, state, cancellationToken);

    public Task FinalizePreparedWorldAsync(
        PreparedWorld world,
        PreparedWorldDisposition disposition,
        CancellationToken cancellationToken = default)
        => VRisingWorldState.FinalizePreparedWorldAsync(world, disposition, cancellationToken);
}
