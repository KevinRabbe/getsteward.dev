namespace SharedWorlds.Infrastructure.BackendSimulation;

public enum SimulatedBackendFailurePoint
{
    BeforeCommitTransaction,
    AfterDurableCommitBeforeResponse
}

public sealed class SimulatedBackendFailureException : Exception
{
    public SimulatedBackendFailureException(SimulatedBackendFailurePoint failurePoint)
        : base($"Injected backend failure at '{failurePoint}'.")
    {
        FailurePoint = failurePoint;
    }

    public SimulatedBackendFailurePoint FailurePoint { get; }
}

public sealed partial class InMemoryBackendContractSimulation
{
    private readonly Dictionary<SimulatedBackendFailurePoint, int> _pendingFailures = new();

    public void FailNext(SimulatedBackendFailurePoint failurePoint)
    {
        lock (_gate)
        {
            _pendingFailures.TryGetValue(failurePoint, out var count);
            _pendingFailures[failurePoint] = count + 1;
        }
    }

    public IdempotentOperationResult<CommitCandidateResult> CommitCandidateIdempotentWithFailureInjection(
        string callerIdentityId,
        string idempotencyKey,
        string worldId,
        long generation,
        string expectedHeadRevisionId,
        string candidateRevisionId)
    {
        ThrowIfInjected(SimulatedBackendFailurePoint.BeforeCommitTransaction);

        var result = CommitCandidateIdempotent(
            callerIdentityId,
            idempotencyKey,
            worldId,
            generation,
            expectedHeadRevisionId,
            candidateRevisionId);

        // This point represents a transport/response failure after both the canonical mutation and
        // idempotency result are already durable. The caller sees failure, but retry/lookup must
        // recover the original logical result rather than executing a second commit.
        ThrowIfInjected(SimulatedBackendFailurePoint.AfterDurableCommitBeforeResponse);
        return result;
    }

    private void ThrowIfInjected(SimulatedBackendFailurePoint failurePoint)
    {
        lock (_gate)
        {
            if (!_pendingFailures.TryGetValue(failurePoint, out var count) || count <= 0)
            {
                return;
            }

            if (count == 1)
            {
                _pendingFailures.Remove(failurePoint);
            }
            else
            {
                _pendingFailures[failurePoint] = count - 1;
            }
        }

        throw new SimulatedBackendFailureException(failurePoint);
    }
}
