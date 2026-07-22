using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Sessions;

namespace SharedWorlds.Infrastructure.Remote;

public sealed record StewardWorldSessionCoordinatorOptions
{
    public StewardWorldSessionCoordinatorOptions(
        TimeSpan heartbeatInterval,
        int acquireTransportAttempts,
        int headRefreshAttempts)
    {
        if (heartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        }

        if (acquireTransportAttempts is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(acquireTransportAttempts));
        }

        if (headRefreshAttempts is < 1 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(headRefreshAttempts));
        }

        HeartbeatInterval = heartbeatInterval;
        AcquireTransportAttempts = acquireTransportAttempts;
        HeadRefreshAttempts = headRefreshAttempts;
    }

    public TimeSpan HeartbeatInterval { get; }
    public int AcquireTransportAttempts { get; }
    public int HeadRefreshAttempts { get; }

    public static StewardWorldSessionCoordinatorOptions FirstReleaseDefaults { get; } = new(
        TimeSpan.FromSeconds(30),
        acquireTransportAttempts: 2,
        headRefreshAttempts: 2);
}

public sealed class StewardWorldUnavailableException : InvalidOperationException
{
    public StewardWorldUnavailableException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class StewardReservationOutcomeUnknownException : IOException
{
    public StewardReservationOutcomeUnknownException(WorldId worldId, Exception innerException)
        : base(
            $"Steward could not determine whether writable authority for World '{worldId}' was acquired. " +
            "No game session was started. Retry after connectivity returns; the backend will recover an already-held reservation safely.",
            innerException)
    {
        WorldId = worldId;
    }

    public WorldId WorldId { get; }
}

/// <summary>
/// Distributed IWorldSessionCoordinator backed by the BE-4 reservation/generation API.
/// It owns only authority/liveness. Candidate byte transfer and canonical commit remain storage work,
/// bridged through <see cref="StewardWritableReservationRegistry"/>.
/// </summary>
public sealed class StewardWorldSessionCoordinator : IWorldSessionCoordinator
{
    private readonly StewardWorldMetadataClient _worlds;
    private readonly StewardAuthorityClient _authority;
    private readonly StewardReservationAbandonClient _abandon;
    private readonly IStewardAccessTokenProvider _accessTokens;
    private readonly IWorkspaceRecoveryStore _recovery;
    private readonly StewardWritableReservationRegistry _reservations;
    private readonly string _installationId;
    private readonly StewardWorldSessionCoordinatorOptions _options;

    public StewardWorldSessionCoordinator(
        StewardWorldMetadataClient worlds,
        StewardAuthorityClient authority,
        StewardReservationAbandonClient abandon,
        IStewardAccessTokenProvider accessTokens,
        IWorkspaceRecoveryStore recovery,
        StewardWritableReservationRegistry reservations,
        string installationId,
        StewardWorldSessionCoordinatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(worlds);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(abandon);
        ArgumentNullException.ThrowIfNull(accessTokens);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(reservations);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);

        _worlds = worlds;
        _authority = authority;
        _abandon = abandon;
        _accessTokens = accessTokens;
        _recovery = recovery;
        _reservations = reservations;
        _installationId = installationId;
        _options = options ?? StewardWorldSessionCoordinatorOptions.FirstReleaseDefaults;
    }

    public async Task<WorldSession> GetSessionAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        var accessToken = await _accessTokens.GetAccessTokenAsync(cancellationToken);
        var reservation = await _authority.GetReservationAsync(worldId, accessToken, cancellationToken);
        if (reservation is null)
        {
            return new WorldSession(
                worldId,
                SessionState.Available,
                null,
                DateTimeOffset.UtcNow);
        }

        var host = new UserIdentity(
            reservation.HolderProvider,
            reservation.HolderExternalId,
            reservation.HolderExternalId);
        return new WorldSession(
            worldId,
            string.Equals(reservation.State, "Uncertain", StringComparison.OrdinalIgnoreCase)
                ? SessionState.RecoveryPending
                : SessionState.Hosting,
            host,
            reservation.BecameUncertainAt ?? reservation.LastHeartbeatAt);
    }

    public async Task<WorldSession> AcquireHostAsync(
        WorldId worldId,
        UserIdentity user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var existing = _reservations.Get(worldId);
        if (existing is not null)
        {
            return new WorldSession(worldId, SessionState.Hosting, user, DateTimeOffset.UtcNow);
        }

        var world = await LoadRequiredWorldAsync(worldId, cancellationToken);
        var expectedHead = world.Head;

        for (var headAttempt = 0; headAttempt < _options.HeadRefreshAttempts; headAttempt++)
        {
            var idempotencyKey = $"acquire-{Guid.NewGuid():N}";
            RemoteReservationAcquireResult? result = null;
            Exception? lastTransportFailure = null;

            for (var transportAttempt = 0;
                 transportAttempt < _options.AcquireTransportAttempts;
                 transportAttempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var accessToken = await _accessTokens.GetAccessTokenAsync(cancellationToken);
                    result = await _authority.AcquireAsync(
                        worldId,
                        _installationId,
                        expectedHead,
                        accessToken,
                        idempotencyKey,
                        cancellationToken);
                    lastTransportFailure = null;
                    break;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    lastTransportFailure = new TimeoutException(
                        "Steward reservation acquisition timed out before the outcome was known.");
                }
                catch (StewardRemoteApiException exception) when (exception.Retryable)
                {
                    lastTransportFailure = exception;
                }
                catch (HttpRequestException exception)
                {
                    lastTransportFailure = exception;
                }
                catch (IOException exception)
                {
                    lastTransportFailure = exception;
                }
            }

            if (result is null)
            {
                throw new StewardReservationOutcomeUnknownException(
                    worldId,
                    lastTransportFailure ?? new IOException("Reservation acquisition outcome is unknown."));
            }

            switch (result.Status)
            {
                case RemoteReservationAcquireStatus.Acquired:
                case RemoteReservationAcquireStatus.AlreadyHeldByCaller:
                    return RegisterAcquiredReservation(worldId, user, result);

                case RemoteReservationAcquireStatus.HeadChanged:
                    world = await LoadRequiredWorldAsync(worldId, cancellationToken);
                    expectedHead = world.Head;
                    continue;

                case RemoteReservationAcquireStatus.WorldBusy:
                    throw new StewardWorldUnavailableException(
                        "WorldBusy",
                        "Another device currently holds writable authority for this World.");

                case RemoteReservationAcquireStatus.WorldUncertain:
                    throw new StewardWorldUnavailableException(
                        "WorldUncertain",
                        "The previous writer is uncertain and must reconnect or be deliberately reclaimed before play can continue.");

                case RemoteReservationAcquireStatus.NotFoundOrUnauthorized:
                    throw new StewardWorldUnavailableException(
                        "WorldNotFoundOrUnauthorized",
                        "The shared World was not found or this Steam identity no longer has access.");

                case RemoteReservationAcquireStatus.IdempotencyKeyConflict:
                    throw new InvalidDataException(
                        "Steward rejected a newly generated reservation idempotency key as conflicting.");

                default:
                    throw new InvalidOperationException("Unexpected remote reservation acquire status.");
            }
        }

        throw new StewardWorldUnavailableException(
            "HeadChanged",
            "The canonical World head changed repeatedly while writable authority was being acquired. Retry from the latest head.");
    }

    public Task RequestHandoffAsync(
        WorldId worldId,
        UserIdentity requestedHost,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "First-release Steward uses flat membership and acquire-when-available authority, not explicit host handoff requests.");

    public Task CompleteHandoffAsync(
        WorldId worldId,
        UserIdentity newHost,
        RevisionId committedRevision,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async Task ReleaseHostAsync(
        WorldId worldId,
        UserIdentity user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var lease = _reservations.Get(worldId);
        if (lease is null)
        {
            return;
        }

        var unresolved = await _recovery.ListAsync(cancellationToken);
        if (unresolved.Any(record =>
                record.WorldId == worldId &&
                record.Status == WorkspaceRecoveryStatus.RecoveryPending))
        {
            // Writable gameplay happened and canonical commit did not complete. Keep heartbeats and
            // the exact generation alive while the desktop process still owns recovery evidence.
            return;
        }

        var accessToken = await _accessTokens.GetAccessTokenAsync(cancellationToken);
        var result = await _abandon.AbandonAsync(
            worldId,
            lease.InstallationId,
            lease.SessionId,
            lease.Generation,
            accessToken,
            cancellationToken);
        if (result is RemoteReservationAbandonStatus.Abandoned or
            RemoteReservationAbandonStatus.NoLongerCurrent)
        {
            _reservations.TryResolve(worldId, lease.SessionId, lease.Generation);
        }
    }

    private async Task<StewardRemoteWorldMetadata> LoadRequiredWorldAsync(
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        var accessToken = await _accessTokens.GetAccessTokenAsync(cancellationToken);
        return await _worlds.GetWorldAsync(worldId, accessToken, cancellationToken)
            ?? throw new StewardWorldUnavailableException(
                "WorldNotFoundOrUnauthorized",
                "The shared World was not found or this Steam identity no longer has access.");
    }

    private WorldSession RegisterAcquiredReservation(
        WorldId worldId,
        UserIdentity user,
        RemoteReservationAcquireResult result)
    {
        var reservation = result.Reservation
            ?? throw new InvalidDataException("Steward acquired writable authority without reservation metadata.");
        if (reservation.WorldId != worldId)
        {
            throw new InvalidDataException("Steward returned a reservation for the wrong World.");
        }

        if (!string.Equals(reservation.InstallationId, _installationId, StringComparison.Ordinal))
        {
            throw new StewardWorldUnavailableException(
                "HeldByAnotherInstallation",
                "This Steam identity already holds the World from another Steward installation.");
        }

        var lease = new StewardWritableReservationLease(
            reservation.WorldId,
            reservation.SessionId,
            reservation.Generation,
            reservation.InstallationId,
            reservation.StartingHead,
            reservation.HolderProvider,
            reservation.HolderExternalId);
        var heartbeatCancellation = new CancellationTokenSource();
        if (!_reservations.TryRegister(lease, heartbeatCancellation))
        {
            heartbeatCancellation.Dispose();
            throw new InvalidOperationException(
                $"A writable reservation is already registered locally for World '{worldId}'.");
        }

        _ = RunHeartbeatLoopAsync(lease, heartbeatCancellation.Token);
        return new WorldSession(
            worldId,
            SessionState.Hosting,
            user,
            reservation.AcquiredAt);
    }

    private async Task RunHeartbeatLoopAsync(
        StewardWritableReservationLease lease,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    var accessToken = await _accessTokens.GetAccessTokenAsync(cancellationToken);
                    var result = await _authority.HeartbeatAsync(
                        lease.WorldId,
                        lease.InstallationId,
                        lease.SessionId,
                        lease.Generation,
                        accessToken,
                        cancellationToken);
                    if (result == RemoteReservationHeartbeatStatus.ReservationMismatch)
                    {
                        _reservations.TryResolve(
                            lease.WorldId,
                            lease.SessionId,
                            lease.Generation);
                        return;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (StewardSessionExpiredException)
                {
                    // Keep the exact local lease. The backend will move it to Uncertain after missed
                    // heartbeats; reauthentication/reconnect may still recover the same generation.
                }
                catch (StewardRemoteApiException)
                {
                    // Connectivity/backend failures are liveness failures, not proof that authority
                    // disappeared. The next heartbeat attempts same-generation reconnect.
                }
                catch (HttpRequestException)
                {
                }
                catch (IOException)
                {
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
