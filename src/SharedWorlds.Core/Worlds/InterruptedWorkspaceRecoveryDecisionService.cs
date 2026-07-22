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
/// Records the user's explicit decision for an Active workspace found after a Steward restart.
/// It does not capture, publish, commit, or delete state itself. Recovery and cleanup then proceed
/// through their existing status-specific services so a crash between the decision and the work
/// remains deterministic and retryable.
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
        EnsureExactEnvironmentWhenWorkspaceExists(record);

        if (!Directory.Exists(record.WorkingDirectory))
        {
            throw new InterruptedWorkspaceRecoveryDecisionException(
                "WorkspaceMissing",
                "The interrupted workspace is missing, so Steward cannot recover uncommitted changes from it.");
        }

        var updated = record with
        {
            Status = WorkspaceRecoveryStatus.RecoveryPending,
            CandidateStateRevisionId = record.CandidateStateRevisionId ?? RevisionId.New(),
            UpdatedAt = DateTimeOffset.UtcNow,
            Reason =
                "The user explicitly chose to recover changes from an interrupted Steward session. " +
                "The canonical World remains unchanged until candidate recovery commits successfully."
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
        EnsureExactEnvironmentWhenWorkspaceExists(record);

        var updated = record with
        {
            Status = WorkspaceRecoveryStatus.CleanupPending,
            UpdatedAt = DateTimeOffset.UtcNow,
            Reason =
                "The user explicitly chose to discard the interrupted workspace. " +
                "The previous canonical World state remains authoritative; only controlled workspace cleanup is allowed."
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

    private static void EnsureExactEnvironmentWhenWorkspaceExists(WorkspaceRecoveryRecord record)
    {
        if (Directory.Exists(record.WorkingDirectory) && record.EnvironmentRevisionId is null)
        {
            throw new InterruptedWorkspaceRecoveryDecisionException(
                "EnvironmentUnknown",
                "This interrupted workspace predates exact environment journaling. Steward will preserve it rather than guessing which environment owns the workspace.");
        }
    }
}
