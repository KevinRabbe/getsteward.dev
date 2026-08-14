using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorldLifecycleResponsibilityTrackerRecoveryTests
{
    [Fact]
    public void PreparationPendingRecordCreatesNonDestructivePreparationGuard()
    {
        var tracker = new WorldLifecycleResponsibilityTracker();
        var worldId = WorldId.New();

        tracker.InitializeFromRecoveryRecords([
            RecoveryRecord(worldId, WorkspaceRecoveryStatus.PreparationPending)
        ]);

        var snapshot = tracker.Current;
        Assert.Equal(WorldLifecycleResponsibilityKind.PreparationInterrupted, snapshot.Kind);
        Assert.Equal(worldId, snapshot.WorldId);
        Assert.Equal(WorldLifecyclePhase.RecoveryNeeded, snapshot.Phase);
        Assert.False(snapshot.CanQuitWithoutGuard);
        Assert.False(snapshot.CanSelfUpdate);
    }

    [Fact]
    public void PreparationPendingOutranksOtherRecoveryResponsibilities()
    {
        var tracker = new WorldLifecycleResponsibilityTracker();
        var preparationWorld = WorldId.New();

        tracker.InitializeFromRecoveryRecords([
            RecoveryRecord(WorldId.New(), WorkspaceRecoveryStatus.RecoveryPending),
            RecoveryRecord(WorldId.New(), WorkspaceRecoveryStatus.Active),
            RecoveryRecord(preparationWorld, WorkspaceRecoveryStatus.PreparationPending)
        ]);

        Assert.Equal(WorldLifecycleResponsibilityKind.PreparationInterrupted, tracker.Current.Kind);
        Assert.Equal(preparationWorld, tracker.Current.WorldId);
    }

    [Fact]
    public void RecoveryPendingRecordCreatesRecoveryGuard()
    {
        var tracker = new WorldLifecycleResponsibilityTracker();
        var worldId = WorldId.New();

        tracker.InitializeFromRecoveryRecords([
            RecoveryRecord(worldId, WorkspaceRecoveryStatus.RecoveryPending)
        ]);

        var snapshot = tracker.Current;
        Assert.Equal(WorldLifecycleResponsibilityKind.RecoveryNeeded, snapshot.Kind);
        Assert.Equal(worldId, snapshot.WorldId);
        Assert.Equal(WorldLifecyclePhase.RecoveryNeeded, snapshot.Phase);
        Assert.False(snapshot.CanQuitWithoutGuard);
        Assert.False(snapshot.CanSelfUpdate);
    }

    [Fact]
    public void RemovingLastDurableRecoveryRecordClearsStaleGuard()
    {
        var tracker = new WorldLifecycleResponsibilityTracker();
        var worldId = WorldId.New();

        tracker.InitializeFromRecoveryRecords([
            RecoveryRecord(worldId, WorkspaceRecoveryStatus.RecoveryPending)
        ]);
        Assert.Equal(WorldLifecycleResponsibilityKind.RecoveryNeeded, tracker.Current.Kind);

        tracker.InitializeFromRecoveryRecords([]);

        var snapshot = tracker.Current;
        Assert.Equal(WorldLifecycleResponsibilityKind.None, snapshot.Kind);
        Assert.Null(snapshot.WorldId);
        Assert.Null(snapshot.Phase);
        Assert.True(snapshot.CanQuitWithoutGuard);
        Assert.True(snapshot.CanSelfUpdate);
    }

    [Fact]
    public void CleanupPendingRecordRemainsGuardedAsActionRequired()
    {
        var tracker = new WorldLifecycleResponsibilityTracker();
        var worldId = WorldId.New();

        tracker.InitializeFromRecoveryRecords([
            RecoveryRecord(worldId, WorkspaceRecoveryStatus.CleanupPending)
        ]);

        var snapshot = tracker.Current;
        Assert.Equal(WorldLifecycleResponsibilityKind.CleanupPending, snapshot.Kind);
        Assert.Equal(worldId, snapshot.WorldId);
        Assert.Equal(WorldLifecyclePhase.CleanupPending, snapshot.Phase);
        Assert.False(snapshot.CanQuitWithoutGuard);
        Assert.False(snapshot.CanSelfUpdate);
    }

    [Fact]
    public void ActiveRecordFoundAfterRestartRequiresExplicitInterruptedSessionDecision()
    {
        var tracker = new WorldLifecycleResponsibilityTracker();
        var worldId = WorldId.New();

        tracker.InitializeFromRecoveryRecords([
            RecoveryRecord(worldId, WorkspaceRecoveryStatus.Active)
        ]);

        var snapshot = tracker.Current;
        Assert.Equal(WorldLifecycleResponsibilityKind.InterruptedSession, snapshot.Kind);
        Assert.Equal(worldId, snapshot.WorldId);
        Assert.Equal(WorldLifecyclePhase.RecoveryNeeded, snapshot.Phase);
        Assert.False(snapshot.CanQuitWithoutGuard);
        Assert.False(snapshot.CanSelfUpdate);
    }

    private static WorkspaceRecoveryRecord RecoveryRecord(
        WorldId worldId,
        WorkspaceRecoveryStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRecoveryRecord(
            WorkspaceId.New(),
            worldId,
            RevisionId.New(),
            "test-adapter",
            Path.Combine(Path.GetTempPath(), "steward-recovery-test"),
            new UserIdentity("test", "user", "Test User"),
            now,
            now,
            status,
            CandidateStateRevisionId: status == WorkspaceRecoveryStatus.RecoveryPending
                ? RevisionId.New()
                : null);
    }
}
