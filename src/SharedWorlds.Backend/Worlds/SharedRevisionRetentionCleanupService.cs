using SharedWorlds.Backend.Transfers;

namespace SharedWorlds.Backend.Worlds;

public sealed record SharedRevisionRetentionCleanupResult(
    int CandidatesInspected,
    int RevisionsRetired,
    int CandidatesNoLongerEligible,
    int CandidatesAlreadyGone,
    int ObjectDeletesCompleted,
    int ObjectDeletesFailed);

/// <summary>
/// Executes BE-D010 cleanup in two durability phases:
/// 1. revalidate and retire revision metadata while durably enqueueing hosted object deletion;
/// 2. delete queued object bytes outside the database transaction and acknowledge the queue row.
///
/// Storage failure therefore leaks bytes temporarily instead of creating a missing authoritative
/// package. Queue rows survive backend restarts and are retried with bounded cadence.
/// </summary>
public sealed class SharedRevisionRetentionCleanupService
{
    private readonly ISharedRevisionRetentionStore _store;
    private readonly IPrivateImmutableObjectStore _objectStore;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SharedRevisionRetentionOptions _options;

    public SharedRevisionRetentionCleanupService(
        ISharedRevisionRetentionStore store,
        IPrivateImmutableObjectStore objectStore,
        Func<DateTimeOffset> utcNow,
        SharedRevisionRetentionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(objectStore);
        ArgumentNullException.ThrowIfNull(utcNow);
        _store = store;
        _objectStore = objectStore;
        _utcNow = utcNow;
        _options = options ?? SharedRevisionRetentionOptions.FirstReleaseDefaults;
    }

    public async Task<SharedRevisionRetentionCleanupResult> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _utcNow();
        var cutoff = now - _options.UncommittedCandidateGrace;
        var candidates = await _store.ListCleanupEligibleAsync(
            cutoff,
            _options.RetainedCanonicalHeadCount,
            _options.CleanupBatchSize,
            cancellationToken);

        var revisionsRetired = 0;
        var noLongerEligible = 0;
        var alreadyGone = 0;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await _store.TryRetireCleanupCandidateAsync(
                candidate,
                cutoff,
                _options.RetainedCanonicalHeadCount,
                now,
                cancellationToken);
            switch (status)
            {
                case RetireSharedRevisionStatus.Retired:
                    revisionsRetired++;
                    break;
                case RetireSharedRevisionStatus.NoLongerEligible:
                    noLongerEligible++;
                    break;
                case RetireSharedRevisionStatus.NotFound:
                    alreadyGone++;
                    break;
                default:
                    throw new InvalidOperationException("Unexpected revision-retirement result.");
            }
        }

        var pendingObjects = await _store.ListPendingObjectCleanupAsync(
            now - _options.ObjectCleanupRetryDelay,
            _options.CleanupBatchSize,
            cancellationToken);
        var objectDeletesCompleted = 0;
        var objectDeletesFailed = 0;

        foreach (var pending in pendingObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _objectStore.DeleteObjectAsync(pending.ObjectKey, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                await _store.TryRecordObjectCleanupFailureAsync(
                    pending.ObjectKey,
                    now,
                    cancellationToken);
                objectDeletesFailed++;
                continue;
            }

            if (await _store.TryCompleteObjectCleanupAsync(pending.ObjectKey, cancellationToken))
            {
                objectDeletesCompleted++;
            }
        }

        return new SharedRevisionRetentionCleanupResult(
            candidates.Count,
            revisionsRetired,
            noLongerEligible,
            alreadyGone,
            objectDeletesCompleted,
            objectDeletesFailed);
    }
}
