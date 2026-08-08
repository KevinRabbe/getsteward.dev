using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Worlds;

/// <summary>
/// Terminal result for the one-way cutover from the legacy Backend.Api reservation authority to
/// peer authority. Retirement never grants peer authority itself; it only proves that Backend.Api
/// can no longer issue or preserve a legacy writable reservation for this World.
/// </summary>
public enum LegacySharedWorldAuthorityRetirementStatus
{
    Retired = 0,
    AlreadyRetired = 1,
    NotFoundOrUnauthorized = 2,
    ReservationMismatch = 3
}

public sealed record LegacySharedWorldAuthorityRetirementResult(
    LegacySharedWorldAuthorityRetirementStatus Status,
    WorldId WorldId,
    ExternalIdentityRef? RetiredHolder,
    string? RetiredInstallationId,
    Guid? RetiredSessionId,
    long? RetiredGeneration,
    DateTimeOffset? RetiredAt);

/// <summary>
/// Durable persistence boundary for permanently disabling the old shared-World reservation writer.
/// Implementations must serialize retirement against the same World row used by legacy acquire and
/// commit, and retirement must be idempotent only for the exact holder/installation/session/generation
/// that performed the original transition.
/// </summary>
public interface ILegacySharedWorldAuthorityRetirementStore
{
    Task<LegacySharedWorldAuthorityRetirementResult> RetireAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        string installationId,
        Guid sessionId,
        long generation,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class LegacySharedWorldAuthorityRetirementService
{
    private readonly ILegacySharedWorldAuthorityRetirementStore _store;
    private readonly Func<DateTimeOffset> _serverNow;
    private readonly SharedWorldAuthorityOptions _options;

    public LegacySharedWorldAuthorityRetirementService(
        ILegacySharedWorldAuthorityRetirementStore store,
        Func<DateTimeOffset> serverNow,
        SharedWorldAuthorityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(serverNow);
        _store = store;
        _serverNow = serverNow;
        _options = options ?? SharedWorldAuthorityOptions.FirstReleaseDefaults;
    }

    public Task<LegacySharedWorldAuthorityRetirementResult> RetireAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        string installationId,
        Guid sessionId,
        long generation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return _store.RetireAsync(
            caller.Subject,
            worldId,
            installationId,
            sessionId,
            generation,
            _serverNow(),
            _options,
            cancellationToken);
    }
}
