using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

public enum WorldLifecycleResponsibilityKind
{
    None,
    ActiveLifecycle,
    RecoveryNeeded,
    CleanupPending
}

public sealed record WorldLifecycleResponsibilitySnapshot(
    WorldLifecycleResponsibilityKind Kind,
    WorldId? WorldId,
    WorldLifecyclePhase? Phase,
    bool CanQuitWithoutGuard,
    bool CanSelfUpdate);

/// <summary>
/// Converts lifecycle phases and durable startup-recovery records into one conservative runtime
/// responsibility signal for tray/Quit/update decisions. It contains no WPF/UI wording.
/// </summary>
public sealed class WorldLifecycleResponsibilityTracker : IWorldLifecycleObserver
{
    private readonly object _gate = new();
    private WorldLifecycleResponsibilityKind _kind;
    private WorldId? _worldId;
    private WorldLifecyclePhase? _phase;

    public WorldLifecycleResponsibilitySnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return CreateSnapshot();
            }
        }
    }

    public void OnPhaseChanged(WorldLifecyclePhaseChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_gate)
        {
            switch (change.Phase)
            {
                case WorldLifecyclePhase.Completed:
                    _kind = WorldLifecycleResponsibilityKind.None;
                    _worldId = null;
                    _phase = null;
                    break;

                case WorldLifecyclePhase.RecoveryNeeded:
                    _kind = WorldLifecycleResponsibilityKind.RecoveryNeeded;
                    _worldId = change.WorldId;
                    _phase = change.Phase;
                    break;

                case WorldLifecyclePhase.CleanupPending:
                    _kind = WorldLifecycleResponsibilityKind.CleanupPending;
                    _worldId = change.WorldId;
                    _phase = change.Phase;
                    break;

                default:
                    _kind = WorldLifecycleResponsibilityKind.ActiveLifecycle;
                    _worldId = change.WorldId;
                    _phase = change.Phase;
                    break;
            }
        }
    }

    /// <summary>
    /// Must run during startup before affected Worlds are presented as Ready. Durable recovery
    /// evidence wins over an in-memory idle default after a previous crash/restart.
    /// </summary>
    public void InitializeFromRecoveryRecords(IEnumerable<WorkspaceRecoveryRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        lock (_gate)
        {
            var selected = records
                .OrderByDescending(record => Priority(record.Status))
                .ThenByDescending(record => record.UpdatedAt)
                .FirstOrDefault();

            if (selected is null)
            {
                return;
            }

            _worldId = selected.WorldId;
            switch (selected.Status)
            {
                case WorkspaceRecoveryStatus.RecoveryPending:
                    _kind = WorldLifecycleResponsibilityKind.RecoveryNeeded;
                    _phase = WorldLifecyclePhase.RecoveryNeeded;
                    break;

                case WorkspaceRecoveryStatus.CleanupPending:
                    _kind = WorldLifecycleResponsibilityKind.CleanupPending;
                    _phase = WorldLifecyclePhase.CleanupPending;
                    break;

                case WorkspaceRecoveryStatus.Active:
                default:
                    // An Active record found after process restart is an interrupted-session
                    // candidate, not evidence that gameplay is still safely supervised.
                    _kind = WorldLifecycleResponsibilityKind.RecoveryNeeded;
                    _phase = WorldLifecyclePhase.RecoveryNeeded;
                    break;
            }
        }
    }

    private WorldLifecycleResponsibilitySnapshot CreateSnapshot()
    {
        var idle = _kind == WorldLifecycleResponsibilityKind.None;
        return new(
            _kind,
            _worldId,
            _phase,
            CanQuitWithoutGuard: idle,
            CanSelfUpdate: idle);
    }

    private static int Priority(WorkspaceRecoveryStatus status)
        => status switch
        {
            WorkspaceRecoveryStatus.RecoveryPending => 3,
            WorkspaceRecoveryStatus.Active => 2,
            WorkspaceRecoveryStatus.CleanupPending => 1,
            _ => 0
        };
}
