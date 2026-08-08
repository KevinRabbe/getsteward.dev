using SharedWorlds.Backend.Identity;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Worlds;

/// <summary>
/// Terminal result for the one-way cutover from the legacy Backend.Api reservation authority to
/// peer authority. Retirement never grants peer authority itself; it only proves that Backend.Api
/// can no longer issue or preserve legacy authorization or writable reservation state for this World.
/// </summary>
public enum LegacySharedWorldAuthorityRetirementStatus
{
    Retired = 0,
    AlreadyRetired = 1,
    NotFoundOrUnauthorized = 2,
    ReservationMismatch = 3,
    AccessStateNotReady = 4
}

public sealed record LegacySharedWorldAuthorityRetirementResult(
    LegacySharedWorldAuthorityRetirementStatus Status,
    WorldId WorldId,
    ExternalIdentityRef? RetiredHolder,
    string? RetiredInstallationId,
    Guid? RetiredSessionId,
    long? RetiredGeneration,
    RevisionId? RetiredStateRevisionId,
    RevisionId? RetiredEnvironmentRevisionId,
    string? RetiredActiveMembersFingerprint,
    DateTimeOffset? RetiredAt);

/// <summary>
/// Durable persistence boundary for permanently disabling the old shared-World authority plane.
/// Implementations must serialize retirement against the same World row used by legacy acquire,
/// commit, and access mutations. Retirement is idempotent only for the exact original holder and
/// reservation tuple, and it may proceed only after transient legacy access state is resolved.
/// </summary>
public interface ILegacySharedWorldAuthorityRetirementStore
{
    Task<LegacySharedWorldAuthorityRetirementResult?> GetAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        CancellationToken cancellationToken = default);

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

    public Task<LegacySharedWorldAuthorityRetirementResult?> GetAsync(
        VerifiedExternalIdentity caller,
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return _store.GetAsync(caller.Subject, worldId, cancellationToken);
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
