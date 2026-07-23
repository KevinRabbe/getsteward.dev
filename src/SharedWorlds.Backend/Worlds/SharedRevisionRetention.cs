using SharedWorlds.Backend.Transfers;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.Worlds;

public sealed record SharedCanonicalHeadRecord(
    WorldId WorldId,
    long Sequence,
    SharedWorldHead Head,
    DateTimeOffset CommittedAt);

public sealed record SharedRevisionCleanupCandidate(
    WorldId WorldId,
    RevisionId RevisionId,
    SharedPackageKind Kind,
    string? HostedObjectKey,
    DateTimeOffset PublishedAt,
    bool WasCanonical);

public sealed record SharedImmutableObjectCleanupRecord(
    string ObjectKey,
    DateTimeOffset EnqueuedAt,
    int AttemptCount,
    DateTimeOffset? LastAttemptAt);

public enum RetireSharedRevisionStatus
{
    Retired,
    NoLongerEligible,
    NotFound
}

public sealed record SharedRevisionRetentionOptions
{
    public SharedRevisionRetentionOptions(
        int retainedCanonicalHeadCount,
        TimeSpan uncommittedCandidateGrace,
        int cleanupBatchSize,
        TimeSpan? objectCleanupRetryDelay = null)
    {
        if (retainedCanonicalHeadCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedCanonicalHeadCount));
        }

        if (uncommittedCandidateGrace <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(uncommittedCandidateGrace));
        }

        if (cleanupBatchSize is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(cleanupBatchSize));
        }

        var retryDelay = objectCleanupRetryDelay ?? TimeSpan.FromMinutes(15);
        if (retryDelay < TimeSpan.FromMinutes(1) || retryDelay > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(objectCleanupRetryDelay));
        }

        RetainedCanonicalHeadCount = retainedCanonicalHeadCount;
        UncommittedCandidateGrace = uncommittedCandidateGrace;
        CleanupBatchSize = cleanupBatchSize;
        ObjectCleanupRetryDelay = retryDelay;
    }

    public int RetainedCanonicalHeadCount { get; }
    public TimeSpan UncommittedCandidateGrace { get; }
    public int CleanupBatchSize { get; }
    public TimeSpan ObjectCleanupRetryDelay { get; }

    public static SharedRevisionRetentionOptions FirstReleaseDefaults { get; } = new(
        retainedCanonicalHeadCount: 3,
        uncommittedCandidateGrace: TimeSpan.FromDays(7),
        cleanupBatchSize: 100,
        objectCleanupRetryDelay: TimeSpan.FromMinutes(15));
}

/// <summary>
/// Reference-safe retention boundary for immutable revision metadata. Cleanup eligibility is derived
/// from canonical commit sequence plus unresolved authority/transfer references; publication time is
/// used only for verified candidates that never became canonical.
///
/// Physical object deletion is two-phase: retiring revision metadata and durably enqueueing its object
/// key are one database transaction, while the object-store delete is retried asynchronously. The
/// backend may therefore retain extra bytes after a storage failure, but it must never delete bytes
/// first and leave authoritative revision metadata pointing at a missing package.
/// </summary>
public interface ISharedRevisionRetentionStore
{
    Task<IReadOnlyList<SharedCanonicalHeadRecord>> ListRecentCanonicalHeadsAsync(
        WorldId worldId,
        int count,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SharedRevisionCleanupCandidate>> ListCleanupEligibleAsync(
        DateTimeOffset uncommittedCandidateCutoff,
        int retainedCanonicalHeadCount,
        int limit,
        CancellationToken cancellationToken = default);

    Task<RetireSharedRevisionStatus> TryRetireCleanupCandidateAsync(
        SharedRevisionCleanupCandidate candidate,
        DateTimeOffset uncommittedCandidateCutoff,
        int retainedCanonicalHeadCount,
        DateTimeOffset retiredAt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SharedImmutableObjectCleanupRecord>> ListPendingObjectCleanupAsync(
        DateTimeOffset retryAtOrBefore,
        int limit,
        CancellationToken cancellationToken = default);

    Task<bool> TryRecordObjectCleanupFailureAsync(
        string objectKey,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken = default);

    Task<bool> TryCompleteObjectCleanupAsync(
        string objectKey,
        CancellationToken cancellationToken = default);
}

public sealed class SharedRevisionRetentionService
{
    private readonly ISharedRevisionRetentionStore _store;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SharedRevisionRetentionOptions _options;

    public SharedRevisionRetentionService(
        ISharedRevisionRetentionStore store,
        Func<DateTimeOffset> utcNow,
        SharedRevisionRetentionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(utcNow);
        _store = store;
        _utcNow = utcNow;
        _options = options ?? SharedRevisionRetentionOptions.FirstReleaseDefaults;
    }

    public Task<IReadOnlyList<SharedCanonicalHeadRecord>> GetRetainedCanonicalHeadsAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        return _store.ListRecentCanonicalHeadsAsync(
            worldId,
            _options.RetainedCanonicalHeadCount,
            cancellationToken);
    }

    public Task<IReadOnlyList<SharedRevisionCleanupCandidate>> ListCleanupEligibleAsync(
        CancellationToken cancellationToken = default)
        => _store.ListCleanupEligibleAsync(
            _utcNow() - _options.UncommittedCandidateGrace,
            _options.RetainedCanonicalHeadCount,
            _options.CleanupBatchSize,
            cancellationToken);

    private static void ValidateWorldId(WorldId worldId)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }
    }
}
