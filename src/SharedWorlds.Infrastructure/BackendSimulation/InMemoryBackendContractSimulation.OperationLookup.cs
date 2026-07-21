namespace SharedWorlds.Infrastructure.BackendSimulation;

public sealed partial class InMemoryBackendContractSimulation
{
    public AcquireReservationResult? GetAcquireReservationOperationResult(
        string callerIdentityId,
        string idempotencyKey)
        => GetRecordedOperationResult<AcquireReservationResult>(
            callerIdentityId,
            "acquire-reservation",
            idempotencyKey);

    public FinalizeTransferResult? GetFinalizeTransferOperationResult(
        string callerIdentityId,
        string idempotencyKey)
        => GetRecordedOperationResult<FinalizeTransferResult>(
            callerIdentityId,
            "finalize-transfer",
            idempotencyKey);

    public CommitCandidateResult? GetCommitCandidateOperationResult(
        string callerIdentityId,
        string idempotencyKey)
        => GetRecordedOperationResult<CommitCandidateResult>(
            callerIdentityId,
            "commit-candidate",
            idempotencyKey);

    private T? GetRecordedOperationResult<T>(
        string callerIdentityId,
        string operation,
        string idempotencyKey)
        where T : class
    {
        ValidateRequired(callerIdentityId, nameof(callerIdentityId));
        ValidateRequired(operation, nameof(operation));
        ValidateRequired(idempotencyKey, nameof(idempotencyKey));

        var key = IdempotencyKey(callerIdentityId, operation, idempotencyKey);

        lock (_gate)
        {
            return _idempotency.TryGetValue(key, out var recorded) && recorded.Result is T typed
                ? typed
                : null;
        }
    }
}
