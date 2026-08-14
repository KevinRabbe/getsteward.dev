using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

public sealed class InterruptedWorkspaceRecoveryDecisionException : InvalidOperationException
{
    public InterruptedWorkspaceRecoveryDecisionException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Records the user's explicit decision for an Active workspace found after a Safe World restart.
/// It does not capture, publish, commit, delete, or locate runtime state itself. Recovery and cleanup
/// proceed through descriptor-aware services so this decision layer never treats a persisted pathname
/// as authority.
/// </summary>
public sealed class InterruptedWorkspaceRecoveryDecisionService
{
    private readonly IWorkspaceRecoveryStore _recovery;

    public InterruptedWorkspaceRecoveryDecisionService(IWorkspaceRecoveryStore recovery)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        _recovery = recovery;
    }

    public async Task<WorkspaceRecoveryRecord> PrepareRecoveryAsync(
        WorldId worldId,
        string adapterId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);

        var record = await LoadActiveAsync(worldId, cancellationToken);
        EnsureAdapter(record, adapterId);
        EnsureExactEnvironmentForRecovery(record);

        // Do not probe WorkingDirectory here. Current records are identity-based and may intentionally
        // persist no machine path at all. The recovery executor resolves the current runtime location
        // and fails closed if the recoverable material is unavailable.
        var updated = record with
        {
            Status = WorkspaceRecoveryStatus.RecoveryPending,
            CandidateStateRevisionId = record.CandidateStateRevisionId ?? RevisionId.New(),
            UpdatedAt = DateTimeOffset.UtcNow,
            Reason =
                "The user explicitly chose to recover changes from an interrupted Safe World session. " +
                "The committed World remains unchanged until descriptor-aware candidate recovery commits successfully."
        };
        await _recovery.SaveAsync(updated, cancellationToken);
        return updated;
    }

    public async Task<WorkspaceRecoveryRecord> PrepareDiscardAsync(
        WorldId worldId,
        string adapterId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);

        var record = await LoadActiveAsync(worldId, cancellationToken);
        EnsureAdapter(record, adapterId);

        // A legacy record with no exact environment cannot safely reconstruct adapter cleanup. Preserve
        // it as abandoned evidence regardless of whether its historical path happens to exist today.
        // Current descriptor-based records always journal the exact environment and may move to the
        // cleanup service, which resolves their current runtime location independently of this decision.
        var cannotCleanExactly = record.EnvironmentRevisionId is null;
        var updated = record with
        {
            Status = cannotCleanExactly
                ? WorkspaceRecoveryStatus.Abandoned
                : WorkspaceRecoveryStatus.CleanupPending,
            UpdatedAt = DateTimeOffset.UtcNow,
            Reason = cannotCleanExactly
                ? "The user explicitly chose to continue from the last safe state. The legacy workspace " +
                  "does not identify its exact environment, so Safe World preserved the journal as abandoned " +
                  "evidence instead of guessing how to clean runtime state. The committed World remains unchanged " +
                  "and this record no longer owns runtime responsibility."
                : "The user explicitly chose to discard the interrupted runtime state. " +
                  "The previous committed World remains authoritative; only descriptor-aware cleanup is allowed."
        };
        await _recovery.SaveAsync(updated, cancellationToken);
        return updated;
    }

    private async Task<WorkspaceRecoveryRecord> LoadActiveAsync(
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        var records = await _recovery.ListAsync(cancellationToken);
        return records
                   .Where(record =>
                       record.WorldId == worldId &&
                       record.Status == WorkspaceRecoveryStatus.Active)
                   .OrderBy(record => record.CreatedAt)
                   .ThenBy(record => record.Id.ToString(), StringComparer.Ordinal)
                   .FirstOrDefault()
               ?? throw new InterruptedWorkspaceRecoveryDecisionException(
                   "InterruptedSessionNotFound",
                   "No interrupted Active workspace exists for this World.");
    }

    private static void EnsureAdapter(WorkspaceRecoveryRecord record, string adapterId)
    {
        if (!string.Equals(record.AdapterId, adapterId, StringComparison.Ordinal))
        {
            throw new InterruptedWorkspaceRecoveryDecisionException(
                "AdapterMismatch",
                "The interrupted workspace belongs to a different game adapter.");
        }
    }

    private static void EnsureExactEnvironmentForRecovery(WorkspaceRecoveryRecord record)
    {
        if (record.EnvironmentRevisionId is null)
        {
            throw new InterruptedWorkspaceRecoveryDecisionException(
                "EnvironmentUnknown",
                "This interrupted workspace predates exact environment journaling. Safe World will preserve it rather than guess which environment owns recoverable changes.");
        }
    }
}
