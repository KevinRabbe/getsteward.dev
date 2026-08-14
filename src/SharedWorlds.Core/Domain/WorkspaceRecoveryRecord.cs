namespace SharedWorlds.Core.Domain;

public enum WorkspaceRecoveryStatus
{
    /// <summary>
    /// Stable recovery identity has been journaled, but Safe World has not yet proven that adapter
    /// preparation/materialization completed. This state is deliberately not cleanup permission.
    /// </summary>
    PreparationPending,

    Active,
    RecoveryPending,
    CleanupPending,
    Abandoned
}

public sealed record WorkspaceRecoveryRecord(
    WorkspaceId Id,
    WorldId WorldId,
    RevisionId BaseStateRevisionId,
    string AdapterId,
    string WorkingDirectory,
    UserIdentity StartedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    WorkspaceRecoveryStatus Status,
    string? Reason = null,
    RevisionId? CandidateStateRevisionId = null,
    RevisionId? EnvironmentRevisionId = null,
    PreparedWorldRecoveryLocation? RecoveryLocation = null);
