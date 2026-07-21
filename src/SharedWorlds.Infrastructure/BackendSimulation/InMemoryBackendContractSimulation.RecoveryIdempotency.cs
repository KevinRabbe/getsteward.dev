namespace SharedWorlds.Infrastructure.BackendSimulation;

public sealed record ContinueFromLastSafeStateOperationResult(
    ContinueFromLastSafeStateStatus Status);

public sealed partial class InMemoryBackendContractSimulation
{
    public IdempotentOperationResult<ReclaimReservationResult> ReclaimUncertainReservationIdempotent(
        string callerIdentityId,
        string idempotencyKey,
        string worldId)
    {
        var fingerprint = Fingerprint(worldId);
        return ExecuteIdempotent(
            callerIdentityId,
            "reclaim-reservation",
            idempotencyKey,
            fingerprint,
            () => Authority.ReclaimUncertainReservation(worldId, callerIdentityId));
    }

    public IdempotentOperationResult<ContinueFromLastSafeStateOperationResult> ContinueFromLastSafeStateIdempotent(
        string callerIdentityId,
        string idempotencyKey,
        string worldId,
        long? expectedGeneration,
        string? candidateRevisionId)
    {
        var fingerprint = Fingerprint(
            worldId,
            expectedGeneration?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none",
            candidateRevisionId ?? "none");

        return ExecuteIdempotent(
            callerIdentityId,
            "continue-last-safe",
            idempotencyKey,
            fingerprint,
            () => new ContinueFromLastSafeStateOperationResult(
                ContinueFromLastSafeState(
                    worldId,
                    callerIdentityId,
                    expectedGeneration,
                    candidateRevisionId)));
    }

    public ReclaimReservationResult? GetReclaimReservationOperationResult(
        string callerIdentityId,
        string idempotencyKey)
        => GetRecordedOperationResult<ReclaimReservationResult>(
            callerIdentityId,
            "reclaim-reservation",
            idempotencyKey);

    public ContinueFromLastSafeStateOperationResult? GetContinueFromLastSafeStateOperationResult(
        string callerIdentityId,
        string idempotencyKey)
        => GetRecordedOperationResult<ContinueFromLastSafeStateOperationResult>(
            callerIdentityId,
            "continue-last-safe",
            idempotencyKey);
}
