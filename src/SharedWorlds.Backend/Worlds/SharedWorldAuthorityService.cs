using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Worlds;

public sealed class SharedWorldAuthorityService
{
    private readonly ISharedWorldAuthorityStore _store;
    private readonly Func<DateTimeOffset> _serverNow;
    private readonly SharedWorldAuthorityOptions _options;

    public SharedWorldAuthorityService(
        ISharedWorldAuthorityStore store,
        Func<DateTimeOffset> serverNow,
        SharedWorldAuthorityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(serverNow);
        _store = store;
        _serverNow = serverNow;
        _options = options ?? SharedWorldAuthorityOptions.FirstReleaseDefaults;
    }

    public Task<AcquireSharedWorldReservationResult> AcquireAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        string installationId,
        SharedWorldHead expectedHead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return _store.AcquireAsync(
            caller.Subject,
            worldId,
            installationId,
            expectedHead,
            _serverNow(),
            _options,
            cancellationToken);
    }

    public Task<SharedWorldReservation?> GetReservationAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return _store.GetReservationAsync(
            caller.Subject,
            worldId,
            _serverNow(),
            _options,
            cancellationToken);
    }

    public Task<SharedWorldHeartbeatStatus> HeartbeatAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        string installationId,
        Guid sessionId,
        long generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return _store.HeartbeatAsync(
            caller.Subject,
            worldId,
            installationId,
            sessionId,
            generation,
            _serverNow(),
            _options,
            cancellationToken);
    }

    public Task<ReclaimSharedWorldReservationResult> ReclaimAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        Guid expectedSessionId,
        long expectedGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return _store.ReclaimAsync(
            caller.Subject,
            worldId,
            expectedSessionId,
            expectedGeneration,
            _serverNow(),
            _options,
            cancellationToken);
    }

    public Task<CommitSharedWorldResult> CommitAsync(
        VerifiedExternalIdentity caller,
        CommitSharedWorldCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(command);
        return _store.CommitAsync(
            caller.Subject,
            command,
            _serverNow(),
            _options,
            cancellationToken);
    }
}
