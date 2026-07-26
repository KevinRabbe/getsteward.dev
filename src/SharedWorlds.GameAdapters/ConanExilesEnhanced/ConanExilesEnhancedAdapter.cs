using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.ConanExilesEnhanced;

public sealed class ConanExilesEnhancedAdapter : IGameAdapter
{
    public string Id => "conan-exiles-enhanced";
    public string DisplayName => "Conan Exiles Enhanced";
    public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.ExactGameVersion;

    public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ConanExilesEnhancedInstallationDiscovery.Discover());
    }

    public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ConanExilesEnhancedWorldDiscovery.Discover(installation));
    }

    public Task<EnvironmentManifest> InspectEnvironmentAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ConanExilesEnhancedEnvironment.Inspect(installation));
    }

    public Task<EnvironmentVerificationReport> VerifyEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ConanExilesEnhancedEnvironment.Verify(installation, requiredEnvironment));
    }

    public async Task<CapturedState> CaptureDetectedWorldAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        var sourcePath = Path.GetFullPath(world.SourcePath);
        if (!ConanExilesEnhancedWorldDiscovery.IsSafelyCapturableDatabase(sourcePath))
        {
            throw new InvalidOperationException(
                $"Conan Exiles Enhanced save slot is not a regular idle current database: {sourcePath}");
        }

        var captured = await ConanExilesEnhancedWorldState.CaptureDetectedWorldAsync(
            world,
            cancellationToken);
        if (ConanExilesEnhancedWorldDiscovery.IsSafelyCapturableDatabase(sourcePath))
        {
            return captured;
        }

        try
        {
            File.Delete(captured.Package.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        throw new InvalidOperationException(
            $"Conan Exiles Enhanced save slot became active while Steward was capturing it: {sourcePath}");
    }

    public Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            ConanExilesEnhancedWorldState.PrepareEnvironment(
                installation,
                requiredEnvironment));
    }

    public Task<CapturedState> CaptureStateAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
        => ConanExilesEnhancedWorldState.CapturePreparedWorldAsync(world, cancellationToken);

    public Task RestoreStateAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken = default)
        => ConanExilesEnhancedWorldState.RestorePreparedWorldAsync(world, state, cancellationToken);

    public Task FinalizePreparedWorldAsync(
        PreparedWorld world,
        PreparedWorldDisposition disposition,
        CancellationToken cancellationToken = default)
        => ConanExilesEnhancedWorldState.FinalizePreparedWorldAsync(world, disposition, cancellationToken);
}
