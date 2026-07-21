namespace SharedWorlds.Infrastructure.BackendSimulation;

public enum SimulatedCandidateRetentionKind
{
    UnresolvedLocal,
    ExplicitlyAbandonedLocal,
    VerifiedRemoteUncommitted,
    PartialTransfer,
    CommittedTemporary,
    UnchangedTemporary
}

public sealed record SimulatedCandidateRetentionSnapshot(
    string CandidateId,
    SimulatedCandidateRetentionKind Kind,
    DateTimeOffset LastActivityAt,
    bool RecoveryPinned,
    bool CleanupEligible);

/// <summary>
/// Deterministic BE-D009 cleanup-eligibility policy. Authority rejection and storage cleanup remain
/// separate: a candidate may be non-canonical while still being retained as recovery evidence.
/// </summary>
public sealed class InMemoryCandidateRetentionPolicy
{
    public static readonly TimeSpan AbandonedLocalGrace = TimeSpan.FromDays(7);
    public static readonly TimeSpan VerifiedRemoteGrace = TimeSpan.FromDays(7);
    public static readonly TimeSpan PartialTransferGrace = TimeSpan.FromHours(24);

    private readonly object _gate = new();
    private readonly Dictionary<string, CandidateRecord> _candidates = new(StringComparer.Ordinal);

    public void Track(
        string candidateId,
        SimulatedCandidateRetentionKind kind,
        DateTimeOffset activityAt,
        bool recoveryPinned = false)
    {
        ValidateRequired(candidateId, nameof(candidateId));

        lock (_gate)
        {
            if (_candidates.TryGetValue(candidateId, out var existing))
            {
                if (existing.Kind != kind ||
                    existing.LastActivityAt != activityAt ||
                    existing.RecoveryPinned != recoveryPinned)
                {
                    throw new InvalidOperationException(
                        $"Candidate '{candidateId}' is already tracked with different retention state. " +
                        "Use the explicit activity, pin, or kind transition operation instead.");
                }

                // Exact creation retry is idempotent.
                return;
            }

            _candidates.Add(
                candidateId,
                new CandidateRecord(
                    candidateId,
                    kind,
                    activityAt,
                    recoveryPinned));
        }
    }

    public bool RecordActivity(string candidateId, DateTimeOffset activityAt)
    {
        ValidateRequired(candidateId, nameof(candidateId));

        lock (_gate)
        {
            if (!_candidates.TryGetValue(candidateId, out var candidate))
            {
                return false;
            }

            candidate.LastActivityAt = activityAt;
            return true;
        }
    }

    public bool SetRecoveryPinned(string candidateId, bool pinned)
    {
        ValidateRequired(candidateId, nameof(candidateId));

        lock (_gate)
        {
            if (!_candidates.TryGetValue(candidateId, out var candidate))
            {
                return false;
            }

            candidate.RecoveryPinned = pinned;
            return true;
        }
    }

    public bool ChangeKind(
        string candidateId,
        SimulatedCandidateRetentionKind kind,
        DateTimeOffset activityAt)
    {
        ValidateRequired(candidateId, nameof(candidateId));

        lock (_gate)
        {
            if (!_candidates.TryGetValue(candidateId, out var candidate))
            {
                return false;
            }

            candidate.Kind = kind;
            candidate.LastActivityAt = activityAt;
            return true;
        }
    }

    public SimulatedCandidateRetentionSnapshot? GetSnapshot(
        string candidateId,
        DateTimeOffset now)
    {
        ValidateRequired(candidateId, nameof(candidateId));

        lock (_gate)
        {
            return _candidates.TryGetValue(candidateId, out var candidate)
                ? ToSnapshot(candidate, now)
                : null;
        }
    }

    private static SimulatedCandidateRetentionSnapshot ToSnapshot(
        CandidateRecord candidate,
        DateTimeOffset now)
        => new(
            candidate.CandidateId,
            candidate.Kind,
            candidate.LastActivityAt,
            candidate.RecoveryPinned,
            IsCleanupEligible(candidate, now));

    private static bool IsCleanupEligible(CandidateRecord candidate, DateTimeOffset now)
    {
        var age = now - candidate.LastActivityAt;

        return candidate.Kind switch
        {
            SimulatedCandidateRetentionKind.UnresolvedLocal => false,
            SimulatedCandidateRetentionKind.ExplicitlyAbandonedLocal =>
                age >= AbandonedLocalGrace,
            SimulatedCandidateRetentionKind.VerifiedRemoteUncommitted =>
                !candidate.RecoveryPinned && age >= VerifiedRemoteGrace,
            SimulatedCandidateRetentionKind.PartialTransfer =>
                age >= PartialTransferGrace,
            SimulatedCandidateRetentionKind.CommittedTemporary => true,
            SimulatedCandidateRetentionKind.UnchangedTemporary => true,
            _ => false
        };
    }

    private static void ValidateRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value is required.", parameterName);
        }
    }

    private sealed class CandidateRecord
    {
        public CandidateRecord(
            string candidateId,
            SimulatedCandidateRetentionKind kind,
            DateTimeOffset lastActivityAt,
            bool recoveryPinned)
        {
            CandidateId = candidateId;
            Kind = kind;
            LastActivityAt = lastActivityAt;
            RecoveryPinned = recoveryPinned;
        }

        public string CandidateId { get; }
        public SimulatedCandidateRetentionKind Kind { get; set; }
        public DateTimeOffset LastActivityAt { get; set; }
        public bool RecoveryPinned { get; set; }
    }
}
