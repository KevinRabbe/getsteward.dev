using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

/// <summary>
/// Creates descriptor-based recovery journals before prepared runtime materialization. New records
/// deliberately do not persist an absolute working directory; WorkingDirectory is retained only as
/// a legacy-schema compatibility field for records created by older Safe World builds.
/// </summary>
public static class WorkspaceRecoveryRecordFactory
{
    public static WorkspaceRecoveryRecord CreatePlanned(
        WorkspaceId workspaceId,
        WorldId worldId,
        RevisionId baseStateRevisionId,
        RevisionId environmentRevisionId,
        string adapterId,
        UserIdentity startedBy,
        PreparedWorldRecoveryLocation recoveryLocation,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        ArgumentNullException.ThrowIfNull(startedBy);
        ArgumentNullException.ThrowIfNull(recoveryLocation);
        recoveryLocation.Validate();

        return new WorkspaceRecoveryRecord(
            Id: workspaceId,
            WorldId: worldId,
            BaseStateRevisionId: baseStateRevisionId,
            AdapterId: adapterId,
            WorkingDirectory: string.Empty,
            StartedBy: startedBy,
            CreatedAt: createdAt,
            UpdatedAt: createdAt,
            Status: WorkspaceRecoveryStatus.PreparationPending,
            Reason: "Recovery identity was journaled before prepared runtime materialization. Materialization has not been confirmed, so this record is not cleanup permission.",
            EnvironmentRevisionId: environmentRevisionId,
            RecoveryLocation: recoveryLocation);
    }
}
