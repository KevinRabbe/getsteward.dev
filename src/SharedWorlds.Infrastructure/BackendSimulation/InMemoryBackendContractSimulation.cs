namespace SharedWorlds.Infrastructure.BackendSimulation;

public enum SimulatedTransferState
{
    Active,
    Finalized,
    Abandoned
}

public enum StartTransferStatus
{
    Started,
    Resumed,
    InvalidRequest,
    Unauthorized
}

public enum UploadTransferPartStatus
{
    Accepted,
    AlreadyAccepted,
    TransferNotFound,
    Unauthorized,
    TransferNotActive,
    PartConflict,
    InvalidPart
}

public enum FinalizeTransferStatus
{
    Finalized,
    AlreadyFinalized,
    TransferNotFound,
    Unauthorized,
    IncompleteOrInvalid
}

public enum AbandonTransferStatus
{
    Abandoned,
    AlreadyAbandoned,
    TransferNotFound,
    Unauthorized
}

public enum IdempotencyExecutionStatus
{
    Executed,
    Replayed,
    KeyReuseConflict
}

public enum ContinueFromLastSafeStateStatus
{
    Completed,
    ReservationMismatch,
    InvalidCandidate,
    Unauthorized
}

public sealed record SimulatedTransferSnapshot(
    string TransferId,
    string WorldId,
    string RevisionId,
    long ExpectedByteSize,
    string ExpectedSha256,
    SimulatedTransferState State,
    IReadOnlyList<int> CompletedPartNumbers);

public sealed record StartTransferResult(
    StartTransferStatus Status,
    SimulatedTransferSnapshot? Transfer);

public sealed record FinalizeTransferResult(
    FinalizeTransferStatus Status,
    PublishCandidateResult? Publication);

public sealed record IdempotentOperationResult<T>(
    IdempotencyExecutionStatus Status,
    T? Result)
    where T : class;

/// <summary>
/// Higher-level BE-1 contract simulation layered over the deterministic World authority.
/// It adds resumable transfer state, idempotent mutation replay, and explicit last-safe recovery
/// without introducing HTTP, Steam, cloud SDKs, or provider-specific storage behavior.
/// </summary>
public sealed class InMemoryBackendContractSimulation
{
    public const long MaximumPackageBytes = 20L * 1024 * 1024 * 1024;
    public const int SuggestedTransferPartBytes = 64 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, TransferRecord> _transfers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IdempotencyRecord> _idempotency = new(StringComparer.Ordinal);
    private readonly HashSet<string> _abandonedCandidates = new(StringComparer.Ordinal);

    public InMemoryBackendContractSimulation(Func<DateTimeOffset> utcNow)
    {
        ArgumentNullException.ThrowIfNull(utcNow);
        Authority = new InMemorySharedWorldBackend(utcNow);
    }

    public InMemorySharedWorldBackend Authority { get; }

    public void CreateWorld(
        string worldId,
        string adapterId,
        string initialStateRevisionId,
        ReadOnlySpan<byte> initialStateBytes,
        string accessManagerIdentityId,
        IEnumerable<string>? additionalMembers = null,
        string? environmentRevisionId = null)
        => Authority.CreateWorld(
            worldId,
            adapterId,
            initialStateRevisionId,
            initialStateBytes,
            accessManagerIdentityId,
            additionalMembers,
            environmentRevisionId);

    public StartTransferResult StartTransfer(
        string transferId,
        string worldId,
        string callerIdentityId,
        string revisionId,
        long expectedByteSize,
        string expectedSha256)
    {
        ValidateRequired(transferId, nameof(transferId));
        ValidateRequired(worldId, nameof(worldId));
        ValidateRequired(callerIdentityId, nameof(callerIdentityId));
        ValidateRequired(revisionId, nameof(revisionId));
        ValidateRequired(expectedSha256, nameof(expectedSha256));

        if (expectedByteSize <= 0 || expectedByteSize > MaximumPackageBytes)
        {
            return new(StartTransferStatus.InvalidRequest, null);
        }

        if (Authority.GetWorld(worldId, callerIdentityId) is null)
        {
            return new(StartTransferStatus.Unauthorized, null);
        }

        lock (_gate)
        {
            if (_transfers.TryGetValue(transferId, out var existing))
            {
                if (!existing.Matches(worldId, callerIdentityId, revisionId, expectedByteSize, expectedSha256))
                {
                    return new(StartTransferStatus.InvalidRequest, null);
                }

                return new(StartTransferStatus.Resumed, ToSnapshot(existing));
            }

            var transfer = new TransferRecord(
                transferId,
                worldId,
                callerIdentityId,
                revisionId,
                expectedByteSize,
                expectedSha256);
            _transfers.Add(transferId, transfer);
            return new(StartTransferStatus.Started, ToSnapshot(transfer));
        }
    }

    public UploadTransferPartStatus UploadTransferPart(
        string transferId,
        string callerIdentityId,
        int partNumber,
        ReadOnlySpan<byte> bytes)
    {
        ValidateRequired(transferId, nameof(transferId));
        ValidateRequired(callerIdentityId, nameof(callerIdentityId));

        if (partNumber < 0 || bytes.IsEmpty)
        {
            return UploadTransferPartStatus.InvalidPart;
        }

        lock (_gate)
        {
            if (!_transfers.TryGetValue(transferId, out var transfer))
            {
                return UploadTransferPartStatus.TransferNotFound;
            }

            if (!string.Equals(transfer.CallerIdentityId, callerIdentityId, StringComparison.Ordinal))
            {
                return UploadTransferPartStatus.Unauthorized;
            }

            if (transfer.State != SimulatedTransferState.Active)
            {
                return UploadTransferPartStatus.TransferNotActive;
            }

            var copy = bytes.ToArray();
            if (transfer.Parts.TryGetValue(partNumber, out var existing))
            {
                return existing.AsSpan().SequenceEqual(copy)
                    ? UploadTransferPartStatus.AlreadyAccepted
                    : UploadTransferPartStatus.PartConflict;
            }

            transfer.Parts.Add(partNumber, copy);
            return UploadTransferPartStatus.Accepted;
        }
    }

    public SimulatedTransferSnapshot? GetTransfer(string transferId, string callerIdentityId)
    {
        ValidateRequired(transferId, nameof(transferId));
        ValidateRequired(callerIdentityId, nameof(callerIdentityId));

        lock (_gate)
        {
            return _transfers.TryGetValue(transferId, out var transfer) &&
                   string.Equals(transfer.CallerIdentityId, callerIdentityId, StringComparison.Ordinal)
                ? ToSnapshot(transfer)
                : null;
        }
    }

    public FinalizeTransferResult FinalizeTransfer(string transferId, string callerIdentityId)
    {
        ValidateRequired(transferId, nameof(transferId));
        ValidateRequired(callerIdentityId, nameof(callerIdentityId));

        TransferRecord transfer;
        byte[] packageBytes;

        lock (_gate)
        {
            if (!_transfers.TryGetValue(transferId, out transfer!))
            {
                return new(FinalizeTransferStatus.TransferNotFound, null);
            }

            if (!string.Equals(transfer.CallerIdentityId, callerIdentityId, StringComparison.Ordinal))
            {
                return new(FinalizeTransferStatus.Unauthorized, null);
            }

            if (transfer.State == SimulatedTransferState.Finalized)
            {
                return new(
                    FinalizeTransferStatus.AlreadyFinalized,
                    new PublishCandidateResult(PublishCandidateStatus.Published, transfer.RevisionId));
            }

            if (transfer.State != SimulatedTransferState.Active || !TryAssemblePackage(transfer, out packageBytes))
            {
                return new(FinalizeTransferStatus.IncompleteOrInvalid, null);
            }
        }

        var publication = Authority.PublishCandidate(
            transfer.WorldId,
            callerIdentityId,
            transfer.RevisionId,
            packageBytes,
            transfer.ExpectedByteSize,
            transfer.ExpectedSha256);

        if (publication.Status != PublishCandidateStatus.Published)
        {
            return new(FinalizeTransferStatus.IncompleteOrInvalid, publication);
        }

        lock (_gate)
        {
            // Re-read under the lock so a concurrent abandon cannot be overwritten by finalization.
            if (!_transfers.TryGetValue(transferId, out var current) ||
                current.State != SimulatedTransferState.Active)
            {
                return current?.State == SimulatedTransferState.Finalized
                    ? new(FinalizeTransferStatus.AlreadyFinalized, publication)
                    : new(FinalizeTransferStatus.IncompleteOrInvalid, publication);
            }

            current.State = SimulatedTransferState.Finalized;
            return new(FinalizeTransferStatus.Finalized, publication);
        }
    }

    public AbandonTransferStatus AbandonTransfer(string transferId, string callerIdentityId)
    {
        ValidateRequired(transferId, nameof(transferId));
        ValidateRequired(callerIdentityId, nameof(callerIdentityId));

        lock (_gate)
        {
            if (!_transfers.TryGetValue(transferId, out var transfer))
            {
                return AbandonTransferStatus.TransferNotFound;
            }

            if (!string.Equals(transfer.CallerIdentityId, callerIdentityId, StringComparison.Ordinal))
            {
                return AbandonTransferStatus.Unauthorized;
            }

            if (transfer.State == SimulatedTransferState.Abandoned)
            {
                return AbandonTransferStatus.AlreadyAbandoned;
            }

            if (transfer.State == SimulatedTransferState.Finalized)
            {
                return AbandonTransferStatus.TransferNotFound;
            }

            transfer.State = SimulatedTransferState.Abandoned;
            return AbandonTransferStatus.Abandoned;
        }
    }

    public IdempotentOperationResult<AcquireReservationResult> AcquireReservationIdempotent(
        string callerIdentityId,
        string idempotencyKey,
        string worldId,
        string deviceId,
        string expectedHeadRevisionId)
    {
        var fingerprint = Fingerprint(worldId, deviceId, expectedHeadRevisionId);
        return ExecuteIdempotent(
            callerIdentityId,
            "acquire-reservation",
            idempotencyKey,
            fingerprint,
            () => Authority.AcquireReservation(worldId, callerIdentityId, deviceId, expectedHeadRevisionId));
    }

    public IdempotentOperationResult<FinalizeTransferResult> FinalizeTransferIdempotent(
        string callerIdentityId,
        string idempotencyKey,
        string transferId)
    {
        var fingerprint = Fingerprint(transferId);
        return ExecuteIdempotent(
            callerIdentityId,
            "finalize-transfer",
            idempotencyKey,
            fingerprint,
            () => FinalizeTransfer(transferId, callerIdentityId));
    }

    public IdempotentOperationResult<CommitCandidateResult> CommitCandidateIdempotent(
        string callerIdentityId,
        string idempotencyKey,
        string worldId,
        long generation,
        string expectedHeadRevisionId,
        string candidateRevisionId)
    {
        var fingerprint = Fingerprint(
            worldId,
            generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            expectedHeadRevisionId,
            candidateRevisionId);
        return ExecuteIdempotent(
            callerIdentityId,
            "commit-candidate",
            idempotencyKey,
            fingerprint,
            () => Authority.CommitCandidate(
                worldId,
                callerIdentityId,
                generation,
                expectedHeadRevisionId,
                candidateRevisionId));
    }

    public ContinueFromLastSafeStateStatus ContinueFromLastSafeState(
        string worldId,
        string callerIdentityId,
        long? expectedGeneration,
        string? candidateRevisionId)
    {
        ValidateRequired(worldId, nameof(worldId));
        ValidateRequired(callerIdentityId, nameof(callerIdentityId));

        var world = Authority.GetWorld(worldId, callerIdentityId);
        if (world is null)
        {
            return ContinueFromLastSafeStateStatus.Unauthorized;
        }

        var reservation = Authority.GetReservation(worldId, callerIdentityId);
        if (reservation is not null)
        {
            if (expectedGeneration is null ||
                reservation.Generation != expectedGeneration.Value ||
                !string.Equals(reservation.HolderIdentityId, callerIdentityId, StringComparison.Ordinal))
            {
                return ContinueFromLastSafeStateStatus.ReservationMismatch;
            }

            if (!Authority.ReleaseReservation(worldId, callerIdentityId, reservation.Generation))
            {
                return ContinueFromLastSafeStateStatus.ReservationMismatch;
            }
        }

        if (!string.IsNullOrWhiteSpace(candidateRevisionId))
        {
            if (string.Equals(candidateRevisionId, world.CurrentStateRevisionId, StringComparison.Ordinal) ||
                Authority.DownloadRevision(worldId, callerIdentityId, candidateRevisionId) is null)
            {
                return ContinueFromLastSafeStateStatus.InvalidCandidate;
            }

            lock (_gate)
            {
                _abandonedCandidates.Add(CandidateKey(worldId, candidateRevisionId));
            }
        }

        return ContinueFromLastSafeStateStatus.Completed;
    }

    public bool IsCandidateAbandoned(string worldId, string revisionId)
    {
        ValidateRequired(worldId, nameof(worldId));
        ValidateRequired(revisionId, nameof(revisionId));

        lock (_gate)
        {
            return _abandonedCandidates.Contains(CandidateKey(worldId, revisionId));
        }
    }

    private IdempotentOperationResult<T> ExecuteIdempotent<T>(
        string callerIdentityId,
        string operation,
        string idempotencyKey,
        string requestFingerprint,
        Func<T> operationFactory)
        where T : class
    {
        ValidateRequired(callerIdentityId, nameof(callerIdentityId));
        ValidateRequired(operation, nameof(operation));
        ValidateRequired(idempotencyKey, nameof(idempotencyKey));
        ArgumentNullException.ThrowIfNull(operationFactory);

        var key = $"{callerIdentityId}\u001f{operation}\u001f{idempotencyKey}";

        lock (_gate)
        {
            if (_idempotency.TryGetValue(key, out var recorded))
            {
                if (!string.Equals(recorded.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
                {
                    return new(IdempotencyExecutionStatus.KeyReuseConflict, null);
                }

                return new(IdempotencyExecutionStatus.Replayed, (T)recorded.Result);
            }

            var result = operationFactory();
            _idempotency.Add(key, new IdempotencyRecord(requestFingerprint, result));
            return new(IdempotencyExecutionStatus.Executed, result);
        }
    }

    private static bool TryAssemblePackage(TransferRecord transfer, out byte[] packageBytes)
    {
        packageBytes = Array.Empty<byte>();
        if (transfer.Parts.Count == 0)
        {
            return false;
        }

        var ordered = transfer.Parts.OrderBy(pair => pair.Key).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            if (ordered[index].Key != index)
            {
                return false;
            }
        }

        var length = ordered.Sum(pair => (long)pair.Value.LongLength);
        if (length != transfer.ExpectedByteSize || length > int.MaxValue)
        {
            return false;
        }

        packageBytes = new byte[(int)length];
        var offset = 0;
        foreach (var part in ordered)
        {
            Buffer.BlockCopy(part.Value, 0, packageBytes, offset, part.Value.Length);
            offset += part.Value.Length;
        }

        return string.Equals(
            InMemorySharedWorldBackend.ComputeSha256(packageBytes),
            transfer.ExpectedSha256,
            StringComparison.OrdinalIgnoreCase);
    }

    private static SimulatedTransferSnapshot ToSnapshot(TransferRecord transfer)
        => new(
            transfer.TransferId,
            transfer.WorldId,
            transfer.RevisionId,
            transfer.ExpectedByteSize,
            transfer.ExpectedSha256,
            transfer.State,
            transfer.Parts.Keys.OrderBy(value => value).ToArray());

    private static string Fingerprint(params string[] values)
        => InMemorySharedWorldBackend.ComputeSha256(
            System.Text.Encoding.UTF8.GetBytes(string.Join('\u001f', values)));

    private static string CandidateKey(string worldId, string revisionId)
        => $"{worldId}\u001f{revisionId}";

    private static void ValidateRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }
    }

    private sealed class TransferRecord
    {
        public TransferRecord(
            string transferId,
            string worldId,
            string callerIdentityId,
            string revisionId,
            long expectedByteSize,
            string expectedSha256)
        {
            TransferId = transferId;
            WorldId = worldId;
            CallerIdentityId = callerIdentityId;
            RevisionId = revisionId;
            ExpectedByteSize = expectedByteSize;
            ExpectedSha256 = expectedSha256;
        }

        public string TransferId { get; }
        public string WorldId { get; }
        public string CallerIdentityId { get; }
        public string RevisionId { get; }
        public long ExpectedByteSize { get; }
        public string ExpectedSha256 { get; }
        public SortedDictionary<int, byte[]> Parts { get; } = new();
        public SimulatedTransferState State { get; set; } = SimulatedTransferState.Active;

        public bool Matches(
            string worldId,
            string callerIdentityId,
            string revisionId,
            long expectedByteSize,
            string expectedSha256)
            => string.Equals(WorldId, worldId, StringComparison.Ordinal) &&
               string.Equals(CallerIdentityId, callerIdentityId, StringComparison.Ordinal) &&
               string.Equals(RevisionId, revisionId, StringComparison.Ordinal) &&
               ExpectedByteSize == expectedByteSize &&
               string.Equals(ExpectedSha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record IdempotencyRecord(string RequestFingerprint, object Result);
}
