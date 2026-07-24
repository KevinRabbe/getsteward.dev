using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Worlds;

public enum WorldLifecycleResponsibilityKind
{
    None,
    ActiveLifecycle,
    InterruptedSession,
    RecoveryNeeded,
    CleanupPending
}

public sealed record WorldLifecycleResponsibilitySnapshot(
    WorldLifecycleResponsibilityKind Kind,
    WorldId? WorldId,
    WorldLifecyclePhase? Phase,
    bool CanQuitWithoutGuard,
    bool CanSelfUpdate,
    ManagedWorldSessionMode? Mode = null);

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
    private ManagedWorldSessionMode? _mode;

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
                    Clear();
                    break;

                case WorldLifecyclePhase.RecoveryNeeded:
                    _kind = WorldLifecycleResponsibilityKind.RecoveryNeeded;
                    _worldId = change.WorldId;
                    _phase = change.Phase;
                    _mode = change.Mode;
                    break;

                case WorldLifecyclePhase.CleanupPending:
                    _kind = WorldLifecycleResponsibilityKind.CleanupPending;
                    _worldId = change.WorldId;
                    _phase = change.Phase;
                    _mode = change.Mode;
                    break;

                default:
                    // AcquiringReservation is deliberately guarded too. The coordinator contract
                    // guarantees that an acquisition exception is surfaced only after it has proven
                    // no writable authority was acquired; the lifecycle then emits Completed.
                    _kind = WorldLifecycleResponsibilityKind.ActiveLifecycle;
                    _worldId = change.WorldId;
                    _phase = change.Phase;
                    _mode = change.Mode;
                    break;
            }
        }
    }

    /// <summary>
    /// Reconciles the in-memory responsibility signal with durable recovery evidence while no normal
    /// lifecycle is being supervised. Startup uses this before Worlds are presented; recovery UI uses
    /// the same operation after a recovery attempt changes or removes the journal.
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
                Clear();
                return;
            }

            _worldId = selected.WorldId;
            _mode = null;
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
                    // An Active record found after process restart is not the same as a known
                    // pending-sync candidate: Steward cannot prove whether gameplay started. Keep it
                    // separately guarded so the UI can require an explicit recover-or-discard decision.
                    _kind = WorldLifecycleResponsibilityKind.InterruptedSession;
                    _phase = WorldLifecyclePhase.RecoveryNeeded;
                    break;
            }
        }
    }

    private void Clear()
    {
        _kind = WorldLifecycleResponsibilityKind.None;
        _worldId = null;
        _phase = null;
        _mode = null;
    }

    private WorldLifecycleResponsibilitySnapshot CreateSnapshot()
    {
        var idle = _kind == WorldLifecycleResponsibilityKind.None;
        return new(
            _kind,
            _worldId,
            _phase,
            CanQuitWithoutGuard: idle,
            CanSelfUpdate: idle,
            Mode: _mode);
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
