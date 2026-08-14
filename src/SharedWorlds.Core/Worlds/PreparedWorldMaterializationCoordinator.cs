using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Storage;

namespace SharedWorlds.Core.Worlds;

public sealed record PreparedWorldMaterialization(
    PreparedWorld PreparedWorld,
    WorkspaceRecoveryRecord RecoveryRecord,
    PreparedWorldPreparationContext PreparationContext);

/// <summary>
/// Owns the crash-safe boundary between recovery planning and prepared-runtime materialization for
/// adapters that implement IPreparedWorldRecoveryPlanner. Stable recovery identity is always durable
/// before Safe World creates managed runtime state or invokes adapter preparation.
/// </summary>
public sealed class PreparedWorldMaterializationCoordinator
{
    private readonly IWorkspaceRecoveryStore _recovery;
    private readonly ManagedWorkspaceStorage _managedWorkspaces;

    public PreparedWorldMaterializationCoordinator(
        IWorkspaceRecoveryStore recovery,
        ManagedWorkspaceStorage managedWorkspaces)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(managedWorkspaces);
        _recovery = recovery;
        _managedWorkspaces = managedWorkspaces;
    }

    public async Task<PreparedWorldMaterialization> MaterializeAsync(
        WorldId worldId,
        RevisionId baseStateRevisionId,
        RevisionId environmentRevisionId,
        IGameAdapter adapter,
        GameInstallation installation,
        EnvironmentManifest environment,
        UserIdentity user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(user);

        if (!string.Equals(adapter.Id, environment.AdapterId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Environment belongs to adapter '{environment.AdapterId}', not '{adapter.Id}'.");
        }

        if (adapter is not IPreparedWorldRecoveryPlanner planner)
        {
            throw new NotSupportedException(
                $"Adapter '{adapter.Id}' has not migrated to pre-materialization recovery planning.");
        }

        var workspaceId = WorkspaceId.New();
        var preparation = new PreparedWorldPreparationContext(
            workspaceId,
            _managedWorkspaces.GetWorkspaceDirectory(workspaceId, adapter.Id));

        var plannedLocation = await planner.PlanPreparedWorldRecoveryAsync(
            installation,
            environment,
            preparation,
            cancellationToken);
        PreparedWorldRecoveryPlanPolicy.ValidatePlan(
            adapter.Id,
            preparation,
            plannedLocation);

        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var record = WorkspaceRecoveryRecordFactory.CreatePlanned(
            workspaceId,
            worldId,
            baseStateRevisionId,
            environmentRevisionId,
            adapter.Id,
            user,
            plannedLocation,
            now);

        // From this write onward there is durable evidence before any managed directory exists and
        // before adapter code may materialize state. Cancellation cannot interrupt that safety fence.
        await _recovery.SaveAsync(record, CancellationToken.None);

        if (plannedLocation.Kind == PreparedWorldRecoveryLocationKind.SafeWorldManaged)
        {
            var created = _managedWorkspaces.Create(workspaceId, adapter.Id);
            var expected = Path.GetFullPath(preparation.ManagedWorkingDirectory);
            if (!string.Equals(
                    Path.GetFullPath(created),
                    expected,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Managed workspace creation did not resolve to the exact path offered during recovery planning.");
            }
        }

        var prepared = await adapter.PrepareEnvironmentAsync(
            installation,
            environment,
            preparation,
            cancellationToken);
        PreparedWorldRecoveryPlanPolicy.ValidateMaterializedResult(
            adapter.Id,
            preparation,
            plannedLocation,
            prepared);

        // Materialization is now proven. Cleanup may be attempted safely if later restore/launch work
        // fails, but the session has not started and therefore this is not Active responsibility yet.
        record = record with
        {
            Status = WorkspaceRecoveryStatus.CleanupPending,
            UpdatedAt = DateTimeOffset.UtcNow,
            Reason = "Prepared runtime materialization completed and matched its journaled recovery identity. Session launch has not begun."
        };
        await _recovery.SaveAsync(record, CancellationToken.None);

        return new PreparedWorldMaterialization(prepared, record, preparation);
    }

    /// <summary>
    /// Promotes a confirmed materialized runtime to Active only after the caller has restored the
    /// canonical starting state and is ready to cross the session-launch boundary.
    /// </summary>
    public async Task<WorkspaceRecoveryRecord> MarkReadyForLaunchAsync(
        WorkspaceRecoveryRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();

        if (record.Status != WorkspaceRecoveryStatus.CleanupPending ||
            record.RecoveryLocation is null)
        {
            throw new InvalidOperationException(
                "Only a descriptor-based materialized runtime with cleanup responsibility can become Active.");
        }

        record.RecoveryLocation.Validate();
        var active = record with
        {
            Status = WorkspaceRecoveryStatus.Active,
            UpdatedAt = DateTimeOffset.UtcNow,
            Reason = null
        };
        await _recovery.SaveAsync(active, CancellationToken.None);
        return active;
    }
}
