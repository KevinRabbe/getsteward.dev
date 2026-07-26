using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.Desktop;

/// <summary>
/// Narrow Desktop composition wrapper used only for a shared managed Host operation. The game adapter
/// keeps ownership of game processes and connection material; the session coordinator keeps ownership
/// of remote joinability evidence. Presence failure never changes writable authority or game lifecycle.
/// </summary>
internal sealed class CoordinatedHostGameAdapter : IGameAdapter
{
    private readonly IGameAdapter _inner;
    private readonly IManagedHostEndpointProvider _endpointProvider;
    private readonly IWorldSessionCoordinator _coordinator;
    private readonly WorldId _worldId;
    private bool _presenceStarted;

    public CoordinatedHostGameAdapter(
        IGameAdapter inner,
        IManagedHostEndpointProvider endpointProvider,
        IWorldSessionCoordinator coordinator,
        WorldId worldId)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(endpointProvider);
        ArgumentNullException.ThrowIfNull(coordinator);
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }

        _inner = inner;
        _endpointProvider = endpointProvider;
        _coordinator = coordinator;
        _worldId = worldId;
    }

    public string Id => _inner.Id;
    public string DisplayName => _inner.DisplayName;
    public GameAdapterCapabilities Capabilities => _inner.Capabilities;

    public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
        CancellationToken cancellationToken = default)
        => _inner.DiscoverInstallationsAsync(cancellationToken);

    public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
        GameInstallation installation,
        CancellationToken cancellationToken = default)
        => _inner.DiscoverWorldsAsync(installation, cancellationToken);

    public Task<EnvironmentManifest> InspectEnvironmentAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
        => _inner.InspectEnvironmentAsync(installation, world, cancellationToken);

    public Task<CapturedState> CaptureDetectedWorldAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken = default)
        => _inner.CaptureDetectedWorldAsync(installation, world, cancellationToken);

    public Task<NativeWorldCreationResult> CreateWorldAsync(
        GameInstallation installation,
        WorldCreationRequest request,
        CancellationToken cancellationToken = default)
        => _inner.CreateWorldAsync(installation, request, cancellationToken);

    public Task<PreparedWorld> PrepareEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
        => _inner.PrepareEnvironmentAsync(installation, requiredEnvironment, cancellationToken);

    public Task<EnvironmentVerificationReport> VerifyEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
        => _inner.VerifyEnvironmentAsync(installation, requiredEnvironment, cancellationToken);

    public Task<EnvironmentRepairResult> RepairEnvironmentAsync(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        CancellationToken cancellationToken = default)
        => _inner.RepairEnvironmentAsync(installation, requiredEnvironment, cancellationToken);

    public Task<CapturedState> CaptureStateAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
        => _inner.CaptureStateAsync(world, cancellationToken);

    public Task RestoreStateAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken = default)
        => _inner.RestoreStateAsync(world, state, cancellationToken);

    public Task<GameSessionHandle> LaunchLocalAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
        => _inner.LaunchLocalAsync(world, cancellationToken);

    public async Task<GameSessionHandle> LaunchHostAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
    {
        await TryCoordinateAsync(() => _coordinator.MarkHostStartingAsync(_worldId, CancellationToken.None));
        _presenceStarted = true;

        try
        {
            var session = await _inner.LaunchHostAsync(world, cancellationToken);
            ManagedHostEndpoint? endpoint = null;
            try
            {
                endpoint = _endpointProvider.GetManagedHostEndpoint(session);
            }
            catch
            {
                // Joinability evidence is deliberately secondary to the already-running host process.
            }

            if (endpoint is not null)
            {
                await TryCoordinateAsync(() =>
                    _coordinator.MarkHostReadyAsync(_worldId, endpoint, CancellationToken.None));
            }

            return session;
        }
        catch
        {
            await EndPresenceAsync();
            throw;
        }
    }

    public Task RequestHostStopAsync(
        GameSessionHandle session,
        CancellationToken cancellationToken = default)
        => _inner.RequestHostStopAsync(session, cancellationToken);

    public Task<GameSessionHandle> LaunchClientAsync(
        PreparedWorld world,
        HostConnection host,
        CancellationToken cancellationToken = default)
        => _inner.LaunchClientAsync(world, host, cancellationToken);

    public Task<JoinCapabilityResult> GetJoinCapabilityAsync(
        PreparedWorld world,
        HostConnection host,
        CancellationToken cancellationToken = default)
        => _inner.GetJoinCapabilityAsync(world, host, cancellationToken);

    public async Task WaitForSessionEndAsync(
        GameSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _inner.WaitForSessionEndAsync(session, cancellationToken);
        }
        finally
        {
            await EndPresenceAsync();
        }
    }

    public Task FinalizePreparedWorldAsync(
        PreparedWorld world,
        PreparedWorldDisposition disposition,
        CancellationToken cancellationToken = default)
        => _inner.FinalizePreparedWorldAsync(world, disposition, cancellationToken);

    private async Task EndPresenceAsync()
    {
        if (!_presenceStarted)
        {
            return;
        }

        _presenceStarted = false;
        await TryCoordinateAsync(() =>
            _coordinator.EndHostPresenceAsync(_worldId, CancellationToken.None));
    }

    private static async Task TryCoordinateAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch
        {
            // Host-presence publication is advisory Join evidence. It must never make an already-started
            // game session look like a failed launch or convert a valid writer reservation into recovery.
        }
    }
}
