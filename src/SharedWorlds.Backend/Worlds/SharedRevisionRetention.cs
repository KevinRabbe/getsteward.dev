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

public sealed record SharedRevisionRetentionOptions
{
    public SharedRevisionRetentionOptions(
        int retainedCanonicalHeadCount,
        TimeSpan uncommittedCandidateGrace,
        int cleanupBatchSize)
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

        RetainedCanonicalHeadCount = retainedCanonicalHeadCount;
        UncommittedCandidateGrace = uncommittedCandidateGrace;
        CleanupBatchSize = cleanupBatchSize;
    }

    public int RetainedCanonicalHeadCount { get; }
    public TimeSpan UncommittedCandidateGrace { get; }
    public int CleanupBatchSize { get; }

    public static SharedRevisionRetentionOptions FirstReleaseDefaults { get; } = new(
        retainedCanonicalHeadCount: 3,
        uncommittedCandidateGrace: TimeSpan.FromDays(7),
        cleanupBatchSize: 100);
}

/// <summary>
/// Reference-safe retention boundary for immutable revision metadata. Cleanup eligibility is derived
/// from canonical commit sequence plus unresolved authority/transfer references; publication time is
/// used only for verified candidates that never became canonical.
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
