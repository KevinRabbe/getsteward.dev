using SharedWorlds.Backend.Transfers;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.Tests.Worlds;

public sealed class SharedRevisionRetentionCleanupServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RetiredHostedRevisionDeletesQueuedObjectAndAcknowledgesQueue()
    {
        var candidate = Candidate("packages/state.package");
        var store = new RetentionStore(candidate);
        var objects = new ObjectStore();
        var service = CreateService(store, objects);

        var result = await service.RunOnceAsync();

        Assert.Equal(1, result.CandidatesInspected);
        Assert.Equal(1, result.RevisionsRetired);
        Assert.Equal(1, result.ObjectDeletesCompleted);
        Assert.Equal(0, result.ObjectDeletesFailed);
        Assert.Equal([candidate.HostedObjectKey!], objects.DeletedObjectKeys);
        Assert.Empty(store.PendingObjects);
    }

    [Fact]
    public async Task ObjectDeleteFailureKeepsDurableQueueAndDefersImmediateRetry()
    {
        var candidate = Candidate("packages/state.package");
        var store = new RetentionStore(candidate);
        var objects = new ObjectStore { FailDeletes = true };
        var service = CreateService(store, objects);

        var failed = await service.RunOnceAsync();

        Assert.Equal(1, failed.RevisionsRetired);
        Assert.Equal(1, failed.ObjectDeletesFailed);
        var pending = Assert.Single(store.PendingObjects);
        Assert.Equal(1, pending.AttemptCount);
        Assert.Equal(Now, pending.LastAttemptAt);

        objects.FailDeletes = false;
        var immediate = await service.RunOnceAsync();
        Assert.Equal(0, immediate.ObjectDeletesCompleted);
        Assert.Single(store.PendingObjects);

        store.NowForListing = Now + TimeSpan.FromMinutes(15);
        var retryService = CreateService(store, objects, () => store.NowForListing);
        var retried = await retryService.RunOnceAsync();

        Assert.Equal(1, retried.ObjectDeletesCompleted);
        Assert.Empty(store.PendingObjects);
    }

    [Fact]
    public async Task RetiredNativeEnvironmentRequiresNoObjectDeletion()
    {
        var candidate = new SharedRevisionCleanupCandidate(
            new WorldId(Guid.NewGuid()),
            new RevisionId(Guid.NewGuid()),
            SharedPackageKind.Environment,
            HostedObjectKey: null,
            Now - TimeSpan.FromDays(8),
            WasCanonical: false);
        var store = new RetentionStore(candidate);
        var objects = new ObjectStore();
        var service = CreateService(store, objects);

        var result = await service.RunOnceAsync();

        Assert.Equal(1, result.RevisionsRetired);
        Assert.Equal(0, result.ObjectDeletesCompleted);
        Assert.Empty(objects.DeletedObjectKeys);
        Assert.Empty(store.PendingObjects);
    }

    private static SharedRevisionRetentionCleanupService CreateService(
        ISharedRevisionRetentionStore store,
        IPrivateImmutableObjectStore objects,
        Func<DateTimeOffset>? utcNow = null)
        => new(
            store,
            objects,
            utcNow ?? (() => Now),
            new SharedRevisionRetentionOptions(
                retainedCanonicalHeadCount: 3,
                uncommittedCandidateGrace: TimeSpan.FromDays(7),
                cleanupBatchSize: 100,
                objectCleanupRetryDelay: TimeSpan.FromMinutes(15)));

    private static SharedRevisionCleanupCandidate Candidate(string objectKey)
        => new(
            new WorldId(Guid.NewGuid()),
            new RevisionId(Guid.NewGuid()),
            SharedPackageKind.State,
            objectKey,
            Now - TimeSpan.FromDays(8),
            WasCanonical: false);

    private sealed class RetentionStore : ISharedRevisionRetentionStore
    {
        private readonly List<SharedRevisionCleanupCandidate> _candidates;
        private readonly Dictionary<string, SharedImmutableObjectCleanupRecord> _pending = new(StringComparer.Ordinal);

        public RetentionStore(params SharedRevisionCleanupCandidate[] candidates)
        {
            _candidates = [.. candidates];
        }

        public DateTimeOffset NowForListing { get; set; } = Now;
        public IReadOnlyCollection<SharedImmutableObjectCleanupRecord> PendingObjects => _pending.Values;

        public Task<IReadOnlyList<SharedCanonicalHeadRecord>> ListRecentCanonicalHeadsAsync(
            WorldId worldId,
            int count,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SharedCanonicalHeadRecord>>([]);

        public Task<IReadOnlyList<SharedRevisionCleanupCandidate>> ListCleanupEligibleAsync(
            DateTimeOffset uncommittedCandidateCutoff,
            int retainedCanonicalHeadCount,
            int limit,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SharedRevisionCleanupCandidate>>([.. _candidates.Take(limit)]);

        public Task<RetireSharedRevisionStatus> TryRetireCleanupCandidateAsync(
            SharedRevisionCleanupCandidate candidate,
            DateTimeOffset uncommittedCandidateCutoff,
            int retainedCanonicalHeadCount,
            DateTimeOffset retiredAt,
            CancellationToken cancellationToken = default)
        {
            if (!_candidates.Remove(candidate))
            {
                return Task.FromResult(RetireSharedRevisionStatus.NotFound);
            }

            if (candidate.HostedObjectKey is { } objectKey)
            {
                _pending.TryAdd(
                    objectKey,
                    new SharedImmutableObjectCleanupRecord(objectKey, retiredAt, 0, null));
            }

            return Task.FromResult(RetireSharedRevisionStatus.Retired);
        }

        public Task<IReadOnlyList<SharedImmutableObjectCleanupRecord>> ListPendingObjectCleanupAsync(
            DateTimeOffset retryAtOrBefore,
            int limit,
            CancellationToken cancellationToken = default)
        {
            var ready = _pending.Values
                .Where(item => item.LastAttemptAt is null || item.LastAttemptAt <= retryAtOrBefore)
                .OrderBy(item => item.LastAttemptAt ?? item.EnqueuedAt)
                .Take(limit)
                .ToArray();
            return Task.FromResult<IReadOnlyList<SharedImmutableObjectCleanupRecord>>(ready);
        }

        public Task<bool> TryRecordObjectCleanupFailureAsync(
            string objectKey,
            DateTimeOffset attemptedAt,
            CancellationToken cancellationToken = default)
        {
            if (!_pending.TryGetValue(objectKey, out var current))
            {
                return Task.FromResult(false);
            }

            _pending[objectKey] = current with
            {
                AttemptCount = current.AttemptCount + 1,
                LastAttemptAt = attemptedAt
            };
            return Task.FromResult(true);
        }

        public Task<bool> TryCompleteObjectCleanupAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_pending.Remove(objectKey));
    }

    private sealed class ObjectStore : IPrivateImmutableObjectStore
    {
        public bool FailDeletes { get; set; }
        public List<string> DeletedObjectKeys { get; } = [];

        public Task DeleteObjectAsync(string objectKey, CancellationToken cancellationToken = default)
        {
            if (FailDeletes)
            {
                throw new IOException("Simulated object-storage outage.");
            }

            DeletedObjectKeys.Add(objectKey);
            return Task.CompletedTask;
        }

        public Task<ImmutableUploadSession> BeginMultipartUploadAsync(
            string objectKey,
            long expectedByteSize,
            string expectedSha256,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableUploadSnapshot?> GetMultipartUploadAsync(
            string providerUploadId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DirectObjectTransferAuthorization> AuthorizeUploadPartAsync(
            string providerUploadId,
            int partNumber,
            long expectedByteSize,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableStoredObject> CompleteMultipartUploadAsync(
            string providerUploadId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task AbortMultipartUploadAsync(
            string providerUploadId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableStoredObject?> InspectObjectAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DirectObjectTransferAuthorization> AuthorizeDownloadAsync(
            string objectKey,
            long expectedByteSize,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
