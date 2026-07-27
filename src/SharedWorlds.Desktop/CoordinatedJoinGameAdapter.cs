using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Desktop;

/// <summary>
/// Desktop-only composition wrapper for a shared automatic Join. It publishes short-lived lobby
/// presence only after the real game client has launched and clears it when the observed client session
/// ends. Presence is best-effort presentation and can never affect Join success or World authority.
/// </summary>
internal sealed class CoordinatedJoinGameAdapter : IGameAdapter
{
    private static readonly TimeSpan PresenceHeartbeatInterval = TimeSpan.FromSeconds(15);

    private readonly IGameAdapter _inner;
    private readonly StewardWorldPlayerPresenceClient _presence;
    private readonly WorldId _worldId;
    private CancellationTokenSource? _presenceHeartbeatCancellation;
    private Task? _presenceHeartbeatTask;

    public CoordinatedJoinGameAdapter(
        IGameAdapter inner,
        StewardWorldPlayerPresenceClient presence,
        WorldId worldId)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(presence);
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }

        _inner = inner;
        _presence = presence;
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

    public Task<GameSessionHandle> LaunchHostAsync(
        PreparedWorld world,
        CancellationToken cancellationToken = default)
        => _inner.LaunchHostAsync(world, cancellationToken);

    public Task RequestHostStopAsync(
        GameSessionHandle session,
        CancellationToken cancellationToken = default)
        => _inner.RequestHostStopAsync(session, cancellationToken);

    public async Task<GameSessionHandle> LaunchClientAsync(
        PreparedWorld world,
        HostConnection host,
        CancellationToken cancellationToken = default)
    {
        var session = await _inner.LaunchClientAsync(world, host, cancellationToken);
        await StartPresenceAsync();
        return session;
    }

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
            await StopPresenceAsync();
        }
    }

    public Task FinalizePreparedWorldAsync(
        PreparedWorld world,
        PreparedWorldDisposition disposition,
        CancellationToken cancellationToken = default)
        => _inner.FinalizePreparedWorldAsync(world, disposition, cancellationToken);

    private async Task StartPresenceAsync()
    {
        await TryPublishPresenceAsync(CancellationToken.None);

        _presenceHeartbeatCancellation?.Cancel();
        _presenceHeartbeatCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _presenceHeartbeatCancellation = cancellation;
        _presenceHeartbeatTask = RunPresenceHeartbeatAsync(cancellation.Token);
    }

    private async Task RunPresenceHeartbeatAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(PresenceHeartbeatInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await TryPublishPresenceAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task TryPublishPresenceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _presence.PublishAsync(_worldId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Lobby presence is presentation-only. A backend/session/presentation failure must never
            // terminate an already-running read-only game client.
        }
    }

    private async Task StopPresenceAsync()
    {
        var cancellation = _presenceHeartbeatCancellation;
        var heartbeat = _presenceHeartbeatTask;
        _presenceHeartbeatCancellation = null;
        _presenceHeartbeatTask = null;

        if (cancellation is not null)
        {
            cancellation.Cancel();
        }

        if (heartbeat is not null)
        {
            try
            {
                await heartbeat;
            }
            catch
            {
                // Best-effort presentation cleanup only.
            }
        }

        cancellation?.Dispose();

        try
        {
            await _presence.ClearAsync(_worldId, CancellationToken.None);
        }
        catch
        {
            // TTL expiration is the fallback if explicit cleanup cannot reach the backend.
        }
    }
}
