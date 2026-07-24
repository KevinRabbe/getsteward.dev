using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Worlds;

public enum SharedWorldHostPresenceState
{
    Starting,
    Ready
}

public sealed record SharedWorldHostPresence(
    WorldId WorldId,
    Guid SessionId,
    long Generation,
    ExternalIdentityRef Holder,
    string InstallationId,
    SharedWorldHostPresenceState State,
    string? Address,
    int? Port,
    string? JoinToken,
    DateTimeOffset UpdatedAt);

public sealed record SharedWorldHostPresenceOptions
{
    public SharedWorldHostPresenceOptions(TimeSpan expiresAfter)
    {
        if (expiresAfter <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAfter));
        }

        ExpiresAfter = expiresAfter;
    }

    public TimeSpan ExpiresAfter { get; }

    public static SharedWorldHostPresenceOptions FirstReleaseDefaults { get; } = new(
        TimeSpan.FromSeconds(45));
}

public enum PublishSharedWorldHostPresenceStatus
{
    Published,
    ReservationMismatch,
    NotFoundOrUnauthorized
}

public interface ISharedWorldHostPresenceStore
{
    /// <summary>
    /// Stores current host evidence when it is still eligible to replace the row for this World.
    /// False means a newer reservation/session already won and the caller must not report success.
    /// </summary>
    Task<bool> TryUpsertAsync(
        SharedWorldHostPresence presence,
        CancellationToken cancellationToken = default);

    Task<SharedWorldHostPresence?> GetAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        WorldId worldId,
        ExternalIdentityRef holder,
        string installationId,
        Guid sessionId,
        long generation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Ephemeral multiplayer-host evidence. This is intentionally separate from writable World authority:
/// a reservation says who may write; host presence says that the current writer actually launched a
/// joinable server. Presence never grants authority and is hidden as soon as its reservation is not
/// current/active or its short heartbeat lifetime expires.
/// </summary>
public sealed class SharedWorldHostPresenceService
{
    private readonly SharedWorldAuthorityService _authority;
    private readonly ISharedWorldHostPresenceStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SharedWorldHostPresenceOptions _options;

    public SharedWorldHostPresenceService(
        SharedWorldAuthorityService authority,
        ISharedWorldHostPresenceStore store,
        Func<DateTimeOffset> clock,
        SharedWorldHostPresenceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        _authority = authority;
        _store = store;
        _clock = clock;
        _options = options ?? SharedWorldHostPresenceOptions.FirstReleaseDefaults;
    }

    public async Task<PublishSharedWorldHostPresenceStatus> PublishAsync(
        VerifiedExternalIdentity caller,
        string callerInstallationId,
        WorldId worldId,
        Guid reservationSessionId,
        long reservationGeneration,
        SharedWorldHostPresenceState state,
        string? address,
        int? port,
        string? joinToken,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(caller);
        ValidateInstallationId(callerInstallationId);
        ValidateReservationIdentity(reservationSessionId, reservationGeneration);
        ValidateConnection(state, address, port, joinToken);

        var reservation = await _authority.GetReservationAsync(
            caller,
            worldId,
            cancellationToken);
        if (reservation is null)
        {
            return PublishSharedWorldHostPresenceStatus.NotFoundOrUnauthorized;
        }

        var callerSubject = caller.Subject;
        if (reservation.State != SharedWorldReservationState.Active ||
            reservation.Holder != callerSubject ||
            !string.Equals(
                reservation.InstallationId,
                callerInstallationId,
                StringComparison.Ordinal) ||
            reservation.SessionId != reservationSessionId ||
            reservation.Generation != reservationGeneration)
        {
            return PublishSharedWorldHostPresenceStatus.ReservationMismatch;
        }

        var now = _clock();
        var stored = await _store.TryUpsertAsync(
            new SharedWorldHostPresence(
                worldId,
                reservationSessionId,
                reservationGeneration,
                callerSubject,
                callerInstallationId,
                state,
                address,
                port,
                joinToken,
                now),
            cancellationToken);
        return stored
            ? PublishSharedWorldHostPresenceStatus.Published
            : PublishSharedWorldHostPresenceStatus.ReservationMismatch;
    }

    public async Task<SharedWorldHostPresence?> GetVisibleAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(caller);
        var reservation = await _authority.GetReservationAsync(
            caller,
            worldId,
            cancellationToken);
        if (reservation is null || reservation.State != SharedWorldReservationState.Active)
        {
            return null;
        }

        var presence = await _store.GetAsync(worldId, cancellationToken);
        if (presence is null ||
            presence.SessionId != reservation.SessionId ||
            presence.Generation != reservation.Generation ||
            presence.Holder != reservation.Holder ||
            !string.Equals(
                presence.InstallationId,
                reservation.InstallationId,
                StringComparison.Ordinal))
        {
            return null;
        }

        var now = _clock();
        return now - presence.UpdatedAt <= _options.ExpiresAfter
            ? presence
            : null;
    }

    public Task<bool> ClearAsync(
        VerifiedExternalIdentity caller,
        string callerInstallationId,
        WorldId worldId,
        Guid reservationSessionId,
        long reservationGeneration,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(caller);
        ValidateInstallationId(callerInstallationId);
        ValidateReservationIdentity(reservationSessionId, reservationGeneration);
        return _store.DeleteAsync(
            worldId,
            caller.Subject,
            callerInstallationId,
            reservationSessionId,
            reservationGeneration,
            cancellationToken);
    }

    private static void ValidateIdentity(VerifiedExternalIdentity caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller.Subject.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller.Subject.ExternalId);
    }

    private static void ValidateInstallationId(string installationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        if (installationId.Length > 256 || installationId.Any(char.IsControl))
        {
            throw new ArgumentException("Installation ID is invalid.", nameof(installationId));
        }
    }

    private static void ValidateReservationIdentity(Guid sessionId, long generation)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Reservation session ID must not be empty.", nameof(sessionId));
        }

        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }
    }

    private static void ValidateConnection(
        SharedWorldHostPresenceState state,
        string? address,
        int? port,
        string? joinToken)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (state == SharedWorldHostPresenceState.Ready && string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("Ready host presence requires a connection address.", nameof(address));
        }

        if (address is not null &&
            (address.Length > 512 || address.Any(char.IsControl) || address.Any(char.IsWhiteSpace)))
        {
            throw new ArgumentException("Host connection address is invalid.", nameof(address));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (joinToken is not null &&
            (joinToken.Length > 2048 || joinToken.Any(char.IsControl)))
        {
            throw new ArgumentException("Host join token is invalid.", nameof(joinToken));
        }
    }
}
