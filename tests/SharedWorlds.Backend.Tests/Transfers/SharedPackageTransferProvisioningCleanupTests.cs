using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.Tests.Transfers;

public sealed class SharedPackageTransferProvisioningCleanupTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExpiredProvisioningRecoversAbortsAndDeletesInOnePass()
    {
        var transfer = ProvisioningTransfer();
        var store = new TransferStore(transfer);
        var objects = new RecoveringObjectStore(transfer.ObjectKey, "provider-orphan-1");
        var cleanup = CreateCleanup(store, objects);

        var result = await cleanup.RunOnceAsync();

        Assert.Equal(1, result.ExpiredProvisioningTransfers);
        Assert.Equal(1, result.PartialUploadsAborted);
        Assert.Equal(1, result.AbandonedTransferRecordsDeleted);
        Assert.Equal(1, objects.BeginCallCount);
        Assert.Equal(1, objects.AbortCallCount);
        Assert.Empty(objects.Uploads);
        Assert.Null(await store.LoadAsync(transfer.Id));
    }

    [Fact]
    public async Task ProviderFailureKeepsClaimedEvidenceAndLaterPassRetriesBeforeDeletion()
    {
        var transfer = ProvisioningTransfer();
        var store = new TransferStore(transfer);
        var objects = new RecoveringObjectStore(transfer.ObjectKey, "provider-orphan-1")
        {
            FailBegin = true
        };
        var cleanup = CreateCleanup(store, objects);

        var failed = await cleanup.RunOnceAsync();

        Assert.Equal(1, failed.ExpiredProvisioningTransfers);
        var claimed = Assert.IsType<SharedPackageTransferRecord>(await store.LoadAsync(transfer.Id));
        Assert.Equal(SharedPackageTransferState.Abandoned, claimed.State);
        Assert.StartsWith("pending:", claimed.ProviderUploadId, StringComparison.Ordinal);
        Assert.Equal(1, objects.BeginCallCount);
        Assert.Equal(0, objects.AbortCallCount);

        objects.FailBegin = false;
        var retried = await cleanup.RunOnceAsync();

        Assert.Equal(0, retried.ExpiredProvisioningTransfers);
        Assert.Equal(1, retried.PartialUploadsAborted);
        Assert.Equal(1, retried.AbandonedTransferRecordsDeleted);
        Assert.Equal(2, objects.BeginCallCount);
        Assert.Equal(1, objects.AbortCallCount);
        Assert.Null(await store.LoadAsync(transfer.Id));
    }

    [Fact]
    public async Task CleanupLosingProvisioningClaimNeverTouchesProvider()
    {
        var transfer = ProvisioningTransfer();
        var store = new TransferStore(transfer)
        {
            LoseProvisioningClaimToActive = true
        };
        var objects = new RecoveringObjectStore(transfer.ObjectKey, "provider-active-1");
        var cleanup = CreateCleanup(store, objects);

        var result = await cleanup.RunOnceAsync();

        Assert.Equal(0, result.ExpiredProvisioningTransfers);
        Assert.Equal(0, objects.BeginCallCount);
        Assert.Equal(0, objects.AbortCallCount);
        var current = Assert.IsType<SharedPackageTransferRecord>(await store.LoadAsync(transfer.Id));
        Assert.Equal(SharedPackageTransferState.Active, current.State);
        Assert.Equal("provider-active-1", current.ProviderUploadId);
    }

    private static SharedPackageTransferCleanupService CreateCleanup(
        ISharedPackageTransferStore store,
        IPrivateImmutableObjectStore objects)
        => new(
            store,
            new EmptyRevisionStore(),
            objects,
            () => Now,
            new SharedPackageTransferCleanupOptions(
                verifiedCandidateRetention: TimeSpan.FromDays(7),
                batchSize: 100));

    private static SharedPackageTransferRecord ProvisioningTransfer()
    {
        var id = SharedPackageTransferId.New();
        return new SharedPackageTransferRecord(
            id,
            WorldId.New(),
            RevisionId.New(),
            SharedPackageKind.State,
            "factorio",
            new ExternalIdentityRef("steam", "76561198000000001"),
            $"packages/{Guid.NewGuid():N}/state/package.bin",
            $"pending:{id.Value:N}",
            4096,
            new string('A', 64),
            null,
            1024,
            4,
            Now - TimeSpan.FromDays(2),
            Now - TimeSpan.FromDays(1),
            SharedPackageTransferState.Provisioning);
    }

    private sealed class TransferStore : ISharedPackageTransferStore
    {
        private readonly object _gate = new();
        private SharedPackageTransferRecord? _record;

        public TransferStore(SharedPackageTransferRecord record)
        {
            _record = record;
        }

        public bool LoseProvisioningClaimToActive { get; set; }

        public Task<bool> TryCreateAsync(
            SharedPackageTransferRecord transfer,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SharedPackageTransferRecord?> LoadAsync(
            SharedPackageTransferId transferId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_record?.Id == transferId ? _record : null);
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
            lock (_gate)
            {
                if (_record is null ||
                    _record.Id != transferId ||
                    _record.Owner != expectedOwner ||
                    _record.State != expectedState)
                {
                    return Task.FromResult(false);
                }

                if (LoseProvisioningClaimToActive && expectedState == SharedPackageTransferState.Provisioning)
                {
                    LoseProvisioningClaimToActive = false;
                    _record = _record with
                    {
                        State = SharedPackageTransferState.Active,
                        ProviderUploadId = "provider-active-1"
                    };
                    return Task.FromResult(false);
                }

                _record = _record with { State = nextState };
                return Task.FromResult(true);
            }
        }

        public Task<IReadOnlyList<SharedPackageTransferRecord>> ListByStateExpiringBeforeAsync(
            SharedPackageTransferState state,
            DateTimeOffset expiresAtOrBefore,
            int limit,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_record is not null &&
                    _record.State == state &&
                    _record.ExpiresAt <= expiresAtOrBefore)
                {
                    return Task.FromResult<IReadOnlyList<SharedPackageTransferRecord>>([_record]);
                }

                return Task.FromResult<IReadOnlyList<SharedPackageTransferRecord>>([]);
            }
        }

        public Task<bool> TryDeleteAsync(
            SharedPackageTransferId transferId,
            ExternalIdentityRef expectedOwner,
            SharedPackageTransferState expectedState,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_record is null ||
                    _record.Id != transferId ||
                    _record.Owner != expectedOwner ||
                    _record.State != expectedState)
                {
                    return Task.FromResult(false);
                }

                _record = null;
                return Task.FromResult(true);
            }
        }
    }

    private sealed class EmptyRevisionStore : ISharedRevisionMetadataStore
    {
        public Task<StoreRevisionMetadataStatus> TryRecordStateRevisionAsync(
            SharedStateRevisionMetadata revision,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StoreRevisionMetadataStatus> TryRecordEnvironmentRevisionAsync(
            SharedEnvironmentRevisionMetadata revision,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SharedStateRevisionMetadata?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<SharedStateRevisionMetadata?>(null);

        public Task<SharedEnvironmentRevisionMetadata?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<SharedEnvironmentRevisionMetadata?>(null);
    }

    private sealed class RecoveringObjectStore : IPrivateImmutableObjectStore
    {
        private readonly string _objectKey;
        private readonly string _providerUploadId;

        public RecoveringObjectStore(string objectKey, string providerUploadId)
        {
            _objectKey = objectKey;
            _providerUploadId = providerUploadId;
            Uploads.Add(providerUploadId);
        }

        public HashSet<string> Uploads { get; } = new(StringComparer.Ordinal);
        public int BeginCallCount { get; private set; }
        public int AbortCallCount { get; private set; }
        public bool FailBegin { get; set; }

        public Task<ImmutableUploadSession> BeginMultipartUploadAsync(
            string objectKey,
            long expectedByteSize,
            string expectedSha256,
            CancellationToken cancellationToken = default)
        {
            BeginCallCount++;
            if (FailBegin)
            {
                throw new IOException("Simulated provider outage.");
            }

            Assert.Equal(_objectKey, objectKey);
            if (!Uploads.Contains(_providerUploadId))
            {
                Uploads.Add(_providerUploadId);
            }

            return Task.FromResult(new ImmutableUploadSession(_providerUploadId, objectKey));
        }

        public Task AbortMultipartUploadAsync(
            string providerUploadId,
            CancellationToken cancellationToken = default)
        {
            AbortCallCount++;
            Uploads.Remove(providerUploadId);
            return Task.CompletedTask;
        }

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

        public Task<ImmutableStoredObject?> InspectObjectAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
            => Task.FromResult<ImmutableStoredObject?>(null);

        public Task<DirectObjectTransferAuthorization> AuthorizeDownloadAsync(
            string objectKey,
            long expectedByteSize,
            DateTimeOffset expiresAt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteObjectAsync(
            string objectKey,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
