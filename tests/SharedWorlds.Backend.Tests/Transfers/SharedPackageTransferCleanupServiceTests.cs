using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.Tests.Transfers;

public sealed class SharedPackageTransferCleanupServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly ExternalIdentityRef Owner = new("steam", "76561198000000001");

    [Fact]
    public async Task ExpiredIncompleteUploadIsAbortedAndTransferRecordDeleted()
    {
        var transfer = CreateTransfer(
            SharedPackageTransferState.Active,
            expiresAt: Now - TimeSpan.FromMinutes(1));
        var transfers = new TransferStore(transfer);
        var revisions = new RevisionStore();
        var objects = new ObjectStore();
        objects.SeedUpload(transfer.ProviderUploadId);
        var service = CreateService(transfers, revisions, objects);

        var result = await service.RunOnceAsync();

        Assert.Equal(1, result.ExpiredActiveTransfers);
        Assert.Equal(1, result.PartialUploadsAborted);
        Assert.Equal(1, result.TransferRecordsDeleted);
        Assert.Null(await transfers.LoadAsync(transfer.Id));
        Assert.False(objects.HasUpload(transfer.ProviderUploadId));
    }

    [Fact]
    public async Task ExpiredVerifiedCandidateBecomesAbandonedAndIsPreserved()
    {
        var transfer = CreateTransfer(
            SharedPackageTransferState.Active,
            expiresAt: Now - TimeSpan.FromMinutes(1));
        var transfers = new TransferStore(transfer);
        var revisions = new RevisionStore();
        var objects = new ObjectStore();
        objects.SeedObject(new ImmutableStoredObject(
            transfer.ObjectKey,
            transfer.ExpectedByteSize,
            transfer.ExpectedSha256));
        var service = CreateService(transfers, revisions, objects);

        var result = await service.RunOnceAsync();

        Assert.Equal(1, result.RetainedVerifiedCandidates);
        var retained = Assert.IsType<SharedPackageTransferRecord>(await transfers.LoadAsync(transfer.Id));
        Assert.Equal(SharedPackageTransferState.Abandoned, retained.State);
        Assert.True(objects.HasObject(transfer.ObjectKey));
        Assert.Equal(0, result.TransferRecordsDeleted);
    }

    [Fact]
    public async Task VerifiedAbandonedCandidateAfterGraceIsOnlyMarkedCleanupEligible()
    {
        var transfer = CreateTransfer(
            SharedPackageTransferState.Abandoned,
            expiresAt: Now - TimeSpan.FromDays(8));
        var transfers = new TransferStore(transfer);
        var revisions = new RevisionStore();
        var objects = new ObjectStore();
        objects.SeedObject(new ImmutableStoredObject(
            transfer.ObjectKey,
            transfer.ExpectedByteSize,
            transfer.ExpectedSha256));
        var service = CreateService(transfers, revisions, objects);

        var result = await service.RunOnceAsync();

        Assert.Equal(1, result.VerifiedCandidatesCleanupEligible);
        Assert.NotNull(await transfers.LoadAsync(transfer.Id));
        Assert.True(objects.HasObject(transfer.ObjectKey));
        Assert.Equal(0, result.TransferRecordsDeleted);
    }

    [Fact]
    public async Task MatchingPublishedRevisionRepairsExpiredTransferToFinalized()
    {
        var transfer = CreateTransfer(
            SharedPackageTransferState.Active,
            expiresAt: Now - TimeSpan.FromMinutes(1));
        var publishedAt = Now - TimeSpan.FromHours(2);
        var transfers = new TransferStore(transfer);
        var revisions = new RevisionStore();
        revisions.Seed(new SharedStateRevisionMetadata(
            transfer.WorldId,
            transfer.RevisionId,
            transfer.AdapterId,
            transfer.ObjectKey,
            transfer.ExpectedByteSize,
            transfer.ExpectedSha256,
            transfer.RequiredEnvironmentRevisionId,
            Owner,
            publishedAt));
        var objects = new ObjectStore();
        objects.SeedUpload(transfer.ProviderUploadId);
        var service = CreateService(transfers, revisions, objects);

        var result = await service.RunOnceAsync();

        Assert.Equal(1, result.RepairedFinalizedTransfers);
        var repaired = Assert.IsType<SharedPackageTransferRecord>(await transfers.LoadAsync(transfer.Id));
        Assert.Equal(SharedPackageTransferState.Finalized, repaired.State);
        Assert.Equal(publishedAt, repaired.FinalizedAt);
        Assert.False(objects.HasUpload(transfer.ProviderUploadId));
    }

    [Fact]
    public async Task MismatchedStoredObjectBecomesIntegrityFailureAndIsPreserved()
    {
        var transfer = CreateTransfer(
            SharedPackageTransferState.Active,
            expiresAt: Now - TimeSpan.FromMinutes(1));
        var transfers = new TransferStore(transfer);
        var revisions = new RevisionStore();
        var objects = new ObjectStore();
        objects.SeedObject(new ImmutableStoredObject(
            transfer.ObjectKey,
            transfer.ExpectedByteSize,
            new string('B', 64)));
        var service = CreateService(transfers, revisions, objects);

        var result = await service.RunOnceAsync();

        Assert.Equal(1, result.IntegrityFailuresPreserved);
        var retained = Assert.IsType<SharedPackageTransferRecord>(await transfers.LoadAsync(transfer.Id));
        Assert.Equal(SharedPackageTransferState.IntegrityFailed, retained.State);
        Assert.True(objects.HasObject(transfer.ObjectKey));
    }

    [Fact]
    public async Task ConflictingPublishedRevisionBecomesPublicationConflictAndIsPreserved()
    {
        var transfer = CreateTransfer(
            SharedPackageTransferState.Active,
            expiresAt: Now - TimeSpan.FromMinutes(1));
        var transfers = new TransferStore(transfer);
        var revisions = new RevisionStore();
        revisions.Seed(new SharedStateRevisionMetadata(
            transfer.WorldId,
            transfer.RevisionId,
            transfer.AdapterId,
            "packages/conflicting.package",
            transfer.ExpectedByteSize,
            transfer.ExpectedSha256,
            transfer.RequiredEnvironmentRevisionId,
            Owner,
            Now - TimeSpan.FromHours(2)));
        var objects = new ObjectStore();
        objects.SeedObject(new ImmutableStoredObject(
            transfer.ObjectKey,
            transfer.ExpectedByteSize,
            transfer.ExpectedSha256));
        var service = CreateService(transfers, revisions, objects);

        var result = await service.RunOnceAsync();

        Assert.Equal(1, result.PublicationConflictsPreserved);
        var retained = Assert.IsType<SharedPackageTransferRecord>(await transfers.LoadAsync(transfer.Id));
        Assert.Equal(SharedPackageTransferState.PublicationConflict, retained.State);
        Assert.True(objects.HasObject(transfer.ObjectKey));
    }

    private static SharedPackageTransferCleanupService CreateService(
        ISharedPackageTransferStore transfers,
        ISharedRevisionMetadataStore revisions,
        IPrivateImmutableObjectStore objects)
        => new(
            transfers,
            revisions,
            objects,
            () => Now,
            new SharedPackageTransferCleanupOptions(
                TimeSpan.FromDays(7),
                batchSize: 100));

    private static SharedPackageTransferRecord CreateTransfer(
        SharedPackageTransferState state,
        DateTimeOffset expiresAt)
        => new(
            SharedPackageTransferId.New(),
            new WorldId(Guid.NewGuid()),
            new RevisionId(Guid.NewGuid()),
            SharedPackageKind.State,
            "factorio",
            Owner,
            $"packages/test/{Guid.NewGuid():N}.package",
            $"provider-{Guid.NewGuid():N}",
            4096,
            new string('A', 64),
            null,
            1024,
            4,
            expiresAt - TimeSpan.FromHours(23),
            expiresAt,
            state);

    private sealed class TransferStore : ISharedPackageTransferStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<SharedPackageTransferId, SharedPackageTransferRecord> _records = [];

        public TransferStore(params SharedPackageTransferRecord[] transfers)
        {
            foreach (var transfer in transfers)
            {
                _records.Add(transfer.Id, transfer);
            }
        }

        public Task<bool> TryCreateAsync(
            SharedPackageTransferRecord transfer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(_records.TryAdd(transfer.Id, transfer));
            }
        }

        public Task<SharedPackageTransferRecord?> LoadAsync(
            SharedPackageTransferId transferId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _records.TryGetValue(transferId, out var transfer);
                return Task.FromResult(transfer);
            }
        }

        public Task<bool> TrySetStateAsync(
            SharedPackageTransferId transferId,
            ExternalIdentityRef expectedOwner,
            SharedPackageTransferState expectedState,
            SharedPackageTransferState nextState,
            DateTimeOffset changedAt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_records.TryGetValue(transferId, out var transfer) ||
                    transfer.Owner != expectedOwner ||
                    transfer.State != expectedState)
                {
                    return Task.FromResult(false);
                }

                _records[transferId] = transfer with
                {
                    State = nextState,
                    FinalizedAt = nextState == SharedPackageTransferState.Finalized
                        ? changedAt
                        : transfer.FinalizedAt
                };
                return Task.FromResult(true);
            }
        }

        public Task<IReadOnlyList<SharedPackageTransferRecord>> ListByStateExpiringBeforeAsync(
            SharedPackageTransferState state,
            DateTimeOffset expiresAtOrBefore,
            int limit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                IReadOnlyList<SharedPackageTransferRecord> result = _records.Values
                    .Where(transfer => transfer.State == state &&
                                       transfer.ExpiresAt <= expiresAtOrBefore)
                    .OrderBy(transfer => transfer.ExpiresAt)
                    .ThenBy(transfer => transfer.Id.Value)
                    .Take(limit)
                    .ToArray();
                return Task.FromResult(result);
            }
        }

        public Task<bool> TryDeleteAsync(
            SharedPackageTransferId transferId,
            ExternalIdentityRef expectedOwner,
            SharedPackageTransferState expectedState,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (!_records.TryGetValue(transferId, out var transfer) ||
                    transfer.Owner != expectedOwner ||
                    transfer.State != expectedState)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(_records.Remove(transferId));
            }
        }
    }

    private sealed class RevisionStore : ISharedRevisionMetadataStore
    {
        private readonly Dictionary<(WorldId, RevisionId), SharedStateRevisionMetadata> _states = [];
        private readonly Dictionary<(WorldId, RevisionId), SharedEnvironmentRevisionMetadata> _environments = [];

        public void Seed(SharedStateRevisionMetadata revision)
            => _states[(revision.WorldId, revision.RevisionId)] = revision;

        public Task<StoreRevisionMetadataStatus> TryRecordStateRevisionAsync(
            SharedStateRevisionMetadata revision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (revision.WorldId, revision.RevisionId);
            if (_states.TryGetValue(key, out var existing))
            {
                return Task.FromResult(existing == revision
                    ? StoreRevisionMetadataStatus.AlreadyRecorded
                    : StoreRevisionMetadataStatus.Conflict);
            }

            _states.Add(key, revision);
            return Task.FromResult(StoreRevisionMetadataStatus.Recorded);
        }

        public Task<StoreRevisionMetadataStatus> TryRecordEnvironmentRevisionAsync(
            SharedEnvironmentRevisionMetadata revision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (revision.WorldId, revision.RevisionId);
            if (_environments.TryGetValue(key, out var existing))
            {
                return Task.FromResult(existing == revision
                    ? StoreRevisionMetadataStatus.AlreadyRecorded
                    : StoreRevisionMetadataStatus.Conflict);
            }

            _environments.Add(key, revision);
            return Task.FromResult(StoreRevisionMetadataStatus.Recorded);
        }

        public Task<SharedStateRevisionMetadata?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _states.TryGetValue((worldId, revisionId), out var revision);
            return Task.FromResult(revision);
        }

        public Task<SharedEnvironmentRevisionMetadata?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _environments.TryGetValue((worldId, revisionId), out var revision);
            return Task.FromResult(revision);
        }
    }

    private sealed class ObjectStore : IPrivateImmutableObjectStore
    {
        private readonly HashSet<string> _uploads = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ImmutableStoredObject> _objects = new(StringComparer.Ordinal);

        public bool HasUpload(string providerUploadId) => _uploads.Contains(providerUploadId);
        public bool HasObject(string objectKey) => _objects.ContainsKey(objectKey);
        public void SeedUpload(string providerUploadId) => _uploads.Add(providerUploadId);
        public void SeedObject(ImmutableStoredObject stored) => _objects[stored.ObjectKey] = stored;

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
        {
            cancellationToken.ThrowIfCancellationRequested();
            _uploads.Remove(providerUploadId);
            return Task.CompletedTask;
        }

        public Task<ImmutableStoredObject?> InspectObjectAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _objects.TryGetValue(objectKey, out var stored);
            return Task.FromResult(stored);
        }

        public Task<DirectObjectTransferAuthorization> AuthorizeDownloadAsync(
            string objectKey,
            long expectedByteSize,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteObjectAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _objects.Remove(objectKey);
            return Task.CompletedTask;
        }
    }
}
