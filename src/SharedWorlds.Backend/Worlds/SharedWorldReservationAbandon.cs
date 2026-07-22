using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Worlds;

public enum AbandonSharedWorldReservationStatus
{
    Abandoned,
    NoLongerCurrent
}

public interface ISharedWorldReservationAbandonStore
{
    Task<AbandonSharedWorldReservationStatus> AbandonAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        string installationId,
        Guid sessionId,
        long generation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves an exact reservation that the current device acquired but no longer needs because the
/// writable game session never started. The operation is naturally convergent: once that exact
/// generation is gone, repeating it cannot affect a newer writer.
/// </summary>
public sealed class SharedWorldReservationAbandonService
{
    private readonly ISharedWorldReservationAbandonStore _store;

    public SharedWorldReservationAbandonService(ISharedWorldReservationAbandonStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public Task<AbandonSharedWorldReservationStatus> AbandonAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        string installationId,
        Guid sessionId,
        long generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session ID is required.", nameof(sessionId));
        }

        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        return _store.AbandonAsync(
            caller.Subject,
            worldId,
            installationId,
            sessionId,
            generation,
            cancellationToken);
    }
}
