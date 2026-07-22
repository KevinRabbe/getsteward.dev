namespace SharedWorlds.Core.Domain;

public enum WorkspaceRecoveryStatus
{
    Active,
    RecoveryPending,
    CleanupPending
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
    RevisionId? CandidateStateRevisionId = null);
