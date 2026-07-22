using SharedWorlds.Backend.Worlds;

namespace SharedWorlds.Backend.Transfers;

public sealed record SharedPackageTransferCleanupOptions
{
    public SharedPackageTransferCleanupOptions(
        TimeSpan verifiedCandidateRetention,
        int batchSize)
    {
        if (verifiedCandidateRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(verifiedCandidateRetention));
        }

        if (batchSize is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        VerifiedCandidateRetention = verifiedCandidateRetention;
        BatchSize = batchSize;
    }

    public TimeSpan VerifiedCandidateRetention { get; }
    public int BatchSize { get; }

    public static SharedPackageTransferCleanupOptions FirstReleaseDefaults { get; } = new(
        TimeSpan.FromDays(7),
        batchSize: 100);
}

public sealed record SharedPackageTransferCleanupResult(
    int ExpiredActiveTransfers,
    int RepairedFinalizedTransfers,
    int PartialUploadsAborted,
    int TransferRecordsDeleted,
    int RetainedVerifiedCandidates,
    int VerifiedCandidatesCleanupEligible,
    int IntegrityFailuresPreserved,
    int PublicationConflictsPreserved)
{
    public int ExpiredProvisioningTransfers { get; init; }
    public int AbandonedTransferRecordsDeleted { get; init; }
}

/// <summary>
/// BE-3 reconciliation/cleanup for transfer-session artifacts only.
///
/// This service may expire incomplete multipart sessions and repair durable transfer state from
/// already-published revision metadata. It deliberately does not delete a verified completed package:
/// after its retention period it is reported as cleanup-eligible, but physical deletion requires a
/// reference-safe authority check outside the transfer session itself.
///
/// Canonical revision retention and World-head authority remain outside this service.
/// </summary>
public sealed class SharedPackageTransferCleanupService
{
    private const string ProvisioningProviderHandlePrefix = "pending:";

    private readonly ISharedPackageTransferStore _transferStore;
    private readonly ISharedRevisionMetadataStore _revisionStore;
    private readonly IPrivateImmutableObjectStore _objectStore;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SharedPackageTransferCleanupOptions _options;

    public SharedPackageTransferCleanupService(
        ISharedPackageTransferStore transferStore,
        ISharedRevisionMetadataStore revisionStore,
        IPrivateImmutableObjectStore objectStore,
        Func<DateTimeOffset> utcNow,
        SharedPackageTransferCleanupOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transferStore);
        ArgumentNullException.ThrowIfNull(revisionStore);
        ArgumentNullException.ThrowIfNull(objectStore);
        ArgumentNullException.ThrowIfNull(utcNow);

        _transferStore = transferStore;
        _revisionStore = revisionStore;
        _objectStore = objectStore;
        _utcNow = utcNow;
        _options = options ?? SharedPackageTransferCleanupOptions.FirstReleaseDefaults;
    }

    public async Task<SharedPackageTransferCleanupResult> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        var counters = new CleanupCounters();
        var now = _utcNow();
        var provisioningClaimsLost = new HashSet<SharedPackageTransferId>();

        // A previous pass may already have claimed an expired Provisioning row as Abandoned but then
        // lost provider connectivity before it could recover/abort the provider upload. Retry those
        // placeholders first so a newly claimed row is attempted only once per cleanup pass.
        var pendingAbandonedProvisioning = await _transferStore.ListByStateExpiringBeforeAsync(
            SharedPackageTransferState.Abandoned,
            now,
            _options.BatchSize,
            cancellationToken);
        foreach (var transfer in pendingAbandonedProvisioning)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasProvisioningPlaceholder(transfer))
            {
                continue;
            }

            await ReconcileAbandonedProvisioningAsync(transfer, counters, cancellationToken);
        }

        var expiredProvisioning = await _transferStore.ListByStateExpiringBeforeAsync(
            SharedPackageTransferState.Provisioning,
            now,
            _options.BatchSize,
            cancellationToken);
        foreach (var transfer in expiredProvisioning)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await _transferStore.TrySetStateAsync(
                    transfer.Id,
                    transfer.Owner,
                    SharedPackageTransferState.Provisioning,
                    SharedPackageTransferState.Abandoned,
                    now,
                    cancellationToken))
            {
                // Another path owns whatever state won this compare-and-set. Do not rediscover that
                // transfer as Active later in this same pass and make provider decisions based on a
                // stale Provisioning snapshot. The next cleanup pass may evaluate the winner normally.
                provisioningClaimsLost.Add(transfer.Id);
                continue;
            }

            counters.ExpiredProvisioningTransfers++;
            await ReconcileAbandonedProvisioningAsync(
                transfer with { State = SharedPackageTransferState.Abandoned },
                counters,
                cancellationToken);
        }

        var expiredActive = await _transferStore.ListByStateExpiringBeforeAsync(
            SharedPackageTransferState.Active,
            now,
            _options.BatchSize,
            cancellationToken);

        foreach (var transfer in expiredActive)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (provisioningClaimsLost.Contains(transfer.Id))
            {
                continue;
            }

            counters.ExpiredActiveTransfers++;
            await ReconcileExpiredActiveAsync(transfer, now, counters, cancellationToken);
        }

        // Reconcile every expired Abandoned transfer on every pass. The retention window controls
        // only whether a verified unreferenced object is cleanup-eligible; it must never delay repair
        // when publication or conflict evidence becomes visible after an expiry race.
        var expiredAbandoned = await _transferStore.ListByStateExpiringBeforeAsync(
            SharedPackageTransferState.Abandoned,
            now,
            _options.BatchSize,
            cancellationToken);
        var cleanupEligibilityCutoff = now - _options.VerifiedCandidateRetention;

        foreach (var transfer in expiredAbandoned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasProvisioningPlaceholder(transfer))
            {
                // Already attempted at the beginning of this pass. Preserve the row when provider
                // recovery failed and retry it on the next pass rather than spinning immediately.
                continue;
            }

            await ReconcileAbandonedAsync(
                transfer,
                now,
                cleanupEligibilityCutoff,
                counters,
                cancellationToken);
        }

        return counters.ToResult();
    }

    private async Task ReconcileAbandonedProvisioningAsync(
        SharedPackageTransferRecord transfer,
        CleanupCounters counters,
        CancellationToken cancellationToken)
    {
        try
        {
            // The provider upload may have been created immediately before the backend crashed. The
            // production S3 facade recovers an existing multipart upload for this exact immutable key;
            // if none remains it creates an empty upload that is immediately aborted below.
            var recovered = await _objectStore.BeginMultipartUploadAsync(
                transfer.ObjectKey,
                transfer.ExpectedByteSize,
                transfer.ExpectedSha256,
                cancellationToken);
            await _objectStore.AbortMultipartUploadAsync(
                recovered.ProviderUploadId,
                cancellationToken);
            counters.PartialUploadsAborted++;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Cleanup must fail closed. Keeping the claimed Abandoned row with its pending handle is
            // the durable retry evidence; deleting it here could orphan an unknown provider upload.
            return;
        }

        if (await _transferStore.TryDeleteAsync(
                transfer.Id,
                transfer.Owner,
                SharedPackageTransferState.Abandoned,
                cancellationToken))
        {
            counters.TransferRecordsDeleted++;
            counters.AbandonedTransferRecordsDeleted++;
        }
    }

    private async Task ReconcileExpiredActiveAsync(
        SharedPackageTransferRecord transfer,
        DateTimeOffset now,
        CleanupCounters counters,
        CancellationToken cancellationToken)
    {
        var reference = await InspectRevisionReferenceAsync(transfer, cancellationToken);
        if (reference.Status == RevisionReferenceStatus.Match)
        {
            if (await _transferStore.TrySetStateAsync(
                    transfer.Id,
                    transfer.Owner,
                    SharedPackageTransferState.Active,
                    SharedPackageTransferState.Finalized,
                    reference.PublishedAt ?? now,
                    cancellationToken))
            {
                counters.RepairedFinalizedTransfers++;
                await _objectStore.AbortMultipartUploadAsync(
                    transfer.ProviderUploadId,
                    cancellationToken);
            }

            return;
        }

        if (reference.Status == RevisionReferenceStatus.Conflict)
        {
            if (await _transferStore.TrySetStateAsync(
                    transfer.Id,
                    transfer.Owner,
                    SharedPackageTransferState.Active,
                    SharedPackageTransferState.PublicationConflict,
                    now,
                    cancellationToken))
            {
                counters.PublicationConflictsPreserved++;
                await _objectStore.AbortMultipartUploadAsync(
                    transfer.ProviderUploadId,
                    cancellationToken);
            }

            return;
        }

        var stored = await _objectStore.InspectObjectAsync(transfer.ObjectKey, cancellationToken);
        if (stored is not null)
        {
            if (!MatchesExpectedObject(stored, transfer))
            {
                if (await _transferStore.TrySetStateAsync(
                        transfer.Id,
                        transfer.Owner,
                        SharedPackageTransferState.Active,
                        SharedPackageTransferState.IntegrityFailed,
                        now,
                        cancellationToken))
                {
                    counters.IntegrityFailuresPreserved++;
                    await _objectStore.AbortMultipartUploadAsync(
                        transfer.ProviderUploadId,
                        cancellationToken);
                }

                return;
            }

            if (await _transferStore.TrySetStateAsync(
                    transfer.Id,
                    transfer.Owner,
                    SharedPackageTransferState.Active,
                    SharedPackageTransferState.Abandoned,
                    now,
                    cancellationToken))
            {
                await _objectStore.AbortMultipartUploadAsync(
                    transfer.ProviderUploadId,
                    cancellationToken);
            }

            return;
        }

        if (!await _transferStore.TrySetStateAsync(
                transfer.Id,
                transfer.Owner,
                SharedPackageTransferState.Active,
                SharedPackageTransferState.Abandoned,
                now,
                cancellationToken))
        {
            return;
        }

        await _objectStore.AbortMultipartUploadAsync(
            transfer.ProviderUploadId,
            cancellationToken);
        counters.PartialUploadsAborted++;

        var objectAfterAbort = await _objectStore.InspectObjectAsync(
            transfer.ObjectKey,
            cancellationToken);
        if (objectAfterAbort is null)
        {
            if (await _transferStore.TryDeleteAsync(
                    transfer.Id,
                    transfer.Owner,
                    SharedPackageTransferState.Abandoned,
                    cancellationToken))
            {
                counters.TransferRecordsDeleted++;
            }

            return;
        }

        if (MatchesExpectedObject(objectAfterAbort, transfer))
        {
            return;
        }

        if (await _transferStore.TrySetStateAsync(
                transfer.Id,
                transfer.Owner,
                SharedPackageTransferState.Abandoned,
                SharedPackageTransferState.IntegrityFailed,
                now,
                cancellationToken))
        {
            counters.IntegrityFailuresPreserved++;
        }
    }

    private async Task ReconcileAbandonedAsync(
        SharedPackageTransferRecord transfer,
        DateTimeOffset now,
        DateTimeOffset cleanupEligibilityCutoff,
        CleanupCounters counters,
        CancellationToken cancellationToken)
    {
        var reference = await InspectRevisionReferenceAsync(transfer, cancellationToken);
        if (reference.Status == RevisionReferenceStatus.Match)
        {
            if (await _transferStore.TrySetStateAsync(
                    transfer.Id,
                    transfer.Owner,
                    SharedPackageTransferState.Abandoned,
                    SharedPackageTransferState.Finalized,
                    reference.PublishedAt ?? now,
                    cancellationToken))
            {
                counters.RepairedFinalizedTransfers++;
            }

            return;
        }

        if (reference.Status == RevisionReferenceStatus.Conflict)
        {
            if (await _transferStore.TrySetStateAsync(
                    transfer.Id,
                    transfer.Owner,
                    SharedPackageTransferState.Abandoned,
                    SharedPackageTransferState.PublicationConflict,
                    now,
                    cancellationToken))
            {
                counters.PublicationConflictsPreserved++;
            }

            return;
        }

        var stored = await _objectStore.InspectObjectAsync(transfer.ObjectKey, cancellationToken);
        if (stored is null)
        {
            await _objectStore.AbortMultipartUploadAsync(
                transfer.ProviderUploadId,
                cancellationToken);
            counters.PartialUploadsAborted++;

            if (await _transferStore.TryDeleteAsync(
                    transfer.Id,
                    transfer.Owner,
                    SharedPackageTransferState.Abandoned,
                    cancellationToken))
            {
                counters.TransferRecordsDeleted++;
            }

            return;
        }

        if (!MatchesExpectedObject(stored, transfer))
        {
            if (await _transferStore.TrySetStateAsync(
                    transfer.Id,
                    transfer.Owner,
                    SharedPackageTransferState.Abandoned,
                    SharedPackageTransferState.IntegrityFailed,
                    now,
                    cancellationToken))
            {
                counters.IntegrityFailuresPreserved++;
            }

            return;
        }

        if (transfer.ExpiresAt <= cleanupEligibilityCutoff)
        {
            // BE-D009: this verified remote candidate has outlived its ordinary grace period, so it is
            // cleanup-eligible. Physical deletion is intentionally not performed here because transfer
            // state alone cannot prove that no authoritative revision/recovery reference exists.
            counters.VerifiedCandidatesCleanupEligible++;
            return;
        }

        counters.RetainedVerifiedCandidates++;
    }

    private async Task<RevisionReferenceInspection> InspectRevisionReferenceAsync(
        SharedPackageTransferRecord transfer,
        CancellationToken cancellationToken)
    {
        if (transfer.Kind == SharedPackageKind.State)
        {
            var state = await _revisionStore.LoadStateRevisionAsync(
                transfer.WorldId,
                transfer.RevisionId,
                cancellationToken);
            if (state is null)
            {
                return RevisionReferenceInspection.None;
            }

            var matches = string.Equals(state.AdapterId, transfer.AdapterId, StringComparison.Ordinal) &&
                          string.Equals(state.PackageObjectKey, transfer.ObjectKey, StringComparison.Ordinal) &&
                          state.ByteSize == transfer.ExpectedByteSize &&
                          string.Equals(state.Sha256, transfer.ExpectedSha256, StringComparison.OrdinalIgnoreCase) &&
                          state.RequiredEnvironmentRevisionId == transfer.RequiredEnvironmentRevisionId;
            return matches
                ? RevisionReferenceInspection.Match(state.PublishedAt)
                : RevisionReferenceInspection.Conflict;
        }

        var environment = await _revisionStore.LoadEnvironmentRevisionAsync(
            transfer.WorldId,
            transfer.RevisionId,
            cancellationToken);
        if (environment is null)
        {
            return RevisionReferenceInspection.None;
        }

        var environmentMatches = string.Equals(environment.AdapterId, transfer.AdapterId, StringComparison.Ordinal) &&
                                 string.Equals(environment.ArtifactReference, transfer.ObjectKey, StringComparison.Ordinal) &&
                                 environment.ByteSize == transfer.ExpectedByteSize &&
                                 string.Equals(
                                     environment.Sha256,
                                     transfer.ExpectedSha256,
                                     StringComparison.OrdinalIgnoreCase);
        return environmentMatches
            ? RevisionReferenceInspection.Match(environment.PublishedAt)
            : RevisionReferenceInspection.Conflict;
    }

    private static bool MatchesExpectedObject(
        ImmutableStoredObject stored,
        SharedPackageTransferRecord transfer)
        => string.Equals(stored.ObjectKey, transfer.ObjectKey, StringComparison.Ordinal) &&
           stored.ByteSize == transfer.ExpectedByteSize &&
           string.Equals(stored.Sha256, transfer.ExpectedSha256, StringComparison.OrdinalIgnoreCase);

    private static bool HasProvisioningPlaceholder(SharedPackageTransferRecord transfer)
        => transfer.ProviderUploadId.StartsWith(
            ProvisioningProviderHandlePrefix,
            StringComparison.Ordinal);

    private enum RevisionReferenceStatus
    {
        None,
        Match,
        Conflict
    }

    private sealed record RevisionReferenceInspection(
        RevisionReferenceStatus Status,
        DateTimeOffset? PublishedAt)
    {
        public static RevisionReferenceInspection None { get; } = new(RevisionReferenceStatus.None, null);
        public static RevisionReferenceInspection Conflict { get; } = new(RevisionReferenceStatus.Conflict, null);
        public static RevisionReferenceInspection Match(DateTimeOffset publishedAt)
            => new(RevisionReferenceStatus.Match, publishedAt);
    }

    private sealed class CleanupCounters
    {
        public int ExpiredProvisioningTransfers { get; set; }
        public int ExpiredActiveTransfers { get; set; }
        public int RepairedFinalizedTransfers { get; set; }
        public int PartialUploadsAborted { get; set; }
        public int TransferRecordsDeleted { get; set; }
        public int AbandonedTransferRecordsDeleted { get; set; }
        public int RetainedVerifiedCandidates { get; set; }
        public int VerifiedCandidatesCleanupEligible { get; set; }
        public int IntegrityFailuresPreserved { get; set; }
        public int PublicationConflictsPreserved { get; set; }

        public SharedPackageTransferCleanupResult ToResult()
            => new(
                ExpiredActiveTransfers,
                RepairedFinalizedTransfers,
                PartialUploadsAborted,
                TransferRecordsDeleted,
                RetainedVerifiedCandidates,
                VerifiedCandidatesCleanupEligible,
                IntegrityFailuresPreserved,
                PublicationConflictsPreserved)
            {
                ExpiredProvisioningTransfers = ExpiredProvisioningTransfers,
                AbandonedTransferRecordsDeleted = AbandonedTransferRecordsDeleted
            };
    }
}
