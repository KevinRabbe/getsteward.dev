using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Core.Tests.Worlds;

public sealed class WorldLifecycleResponsibilityTrackerTests
{
    [Fact]
    public void ReservationAcquisitionAttemptDoesNotClaimWritableResponsibilityBeforeSuccess()
    {
        var tracker = new WorldLifecycleResponsibilityTracker();
        var worldId = WorldId.New();

        tracker.OnPhaseChanged(Change(worldId, WorldLifecyclePhase.AcquiringReservation));
        var snapshot = tracker.Current;

        Assert.Equal(WorldLifecycleResponsibilityKind.None, snapshot.Kind);
        Assert.Null(snapshot.WorldId);
        Assert.Null(snapshot.Phase);
        Assert.True(snapshot.CanQuitWithoutGuard);
        Assert.True(snapshot.CanSelfUpdate);

        tracker.OnPhaseChanged(Change(worldId, WorldLifecyclePhase.ResolvingWorld));
        var acquired = tracker.Current;

        Assert.Equal(WorldLifecycleResponsibilityKind.ActiveLifecycle, acquired.Kind);
        Assert.Equal(worldId, acquired.WorldId);
        Assert.Equal(WorldLifecyclePhase.ResolvingWorld, acquired.Phase);
        Assert.False(acquired.CanQuitWithoutGuard);
        Assert.False(acquired.CanSelfUpdate);
    }

    [Fact]
    public void ActiveLifecycleBlocksUnguardedQuitAndSelfUpdateUntilCompleted()
    {
        var tracker = new WorldLifecycleResponsibilityTracker();
        var worldId = WorldId.New();

        tracker.OnPhaseChanged(Change(worldId, WorldLifecyclePhase.Running));
        var active = tracker.Current;

        Assert.Equal(WorldLifecycleResponsibilityKind.ActiveLifecycle, active.Kind);
        Assert.Equal(worldId, active.WorldId);
        Assert.False(active.CanQuitWithoutGuard);
        Assert.False(active.CanSelfUpdate);

        tracker.OnPhaseChanged(Change(worldId, WorldLifecyclePhase.Completed));
        var completed = tracker.Current;

        Assert.Equal(WorldLifecycleResponsibilityKind.None, completed.Kind);
        Assert.Null(completed.WorldId);
        Assert.True(completed.CanQuitWithoutGuard);
        Assert.True(completed.CanSelfUpdate);
    }

    [Fact]
    public void RecoveryNeededRemainsGuardedAfterLifecycleFailure()
    {
        var tracker = new WorldLifecycleResponsibilityTracker();
        var worldId = WorldId.New();

        tracker.OnPhaseChanged(Change(worldId, WorldLifecyclePhase.RecoveryNeeded));
        var snapshot = tracker.Current;

        Assert.Equal(WorldLifecycleResponsibilityKind.RecoveryNeeded, snapshot.Kind);
        Assert.Equal(WorldLifecyclePhase.RecoveryNeeded, snapshot.Phase);
        Assert.False(snapshot.CanQuitWithoutGuard);
        Assert.False(snapshot.CanSelfUpdate);
    }

    [Fact]
    public void CleanupPendingDoesNotPretendLifecycleIsFullyIdle()
    {
        var tracker = new WorldLifecycleResponsibilityTracker();
        var worldId = WorldId.New();

        tracker.OnPhaseChanged(Change(worldId, WorldLifecyclePhase.CleanupPending));
        var snapshot = tracker.Current;

        Assert.Equal(WorldLifecycleResponsibilityKind.CleanupPending, snapshot.Kind);
        Assert.False(snapshot.CanQuitWithoutGuard);
        Assert.False(snapshot.CanSelfUpdate);
    }

    [Fact]
    public void StartupActiveRecoveryRecordBecomesRecoveryNeededBeforeReady()
    {
        var tracker = new WorldLifecycleResponsibilityTracker();
        var worldId = WorldId.New();
        var user = new UserIdentity("steam", "steam-a");
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

        tracker.InitializeFromRecoveryRecords(new[]
        {
            new WorkspaceRecoveryRecord(
                WorkspaceId.New(),
                worldId,
                RevisionId.New(),
                "test-adapter",
                "C:/Recovery",
                user,
                now,
                now,
                WorkspaceRecoveryStatus.Active)
        });

        var snapshot = tracker.Current;
        Assert.Equal(WorldLifecycleResponsibilityKind.RecoveryNeeded, snapshot.Kind);
        Assert.Equal(worldId, snapshot.WorldId);
        Assert.Equal(WorldLifecyclePhase.RecoveryNeeded, snapshot.Phase);
        Assert.False(snapshot.CanQuitWithoutGuard);
    }

    [Fact]
    public void StartupChoosesHighestRiskRecoveryResponsibility()
    {
        var tracker = new WorldLifecycleResponsibilityTracker();
        var cleanupWorld = WorldId.New();
        var recoveryWorld = WorldId.New();
        var user = new UserIdentity("steam", "steam-a");
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

        tracker.InitializeFromRecoveryRecords(new[]
        {
            new WorkspaceRecoveryRecord(
                WorkspaceId.New(),
                cleanupWorld,
                RevisionId.New(),
                "test-adapter",
                "C:/Cleanup",
                user,
                now,
                now.AddMinutes(10),
                WorkspaceRecoveryStatus.CleanupPending),
            new WorkspaceRecoveryRecord(
                WorkspaceId.New(),
                recoveryWorld,
                RevisionId.New(),
                "test-adapter",
                "C:/Recovery",
                user,
                now,
                now,
                WorkspaceRecoveryStatus.RecoveryPending)
        });

        var snapshot = tracker.Current;
        Assert.Equal(WorldLifecycleResponsibilityKind.RecoveryNeeded, snapshot.Kind);
        Assert.Equal(recoveryWorld, snapshot.WorldId);
    }

    private static WorldLifecyclePhaseChange Change(WorldId worldId, WorldLifecyclePhase phase)
        => new(
            worldId,
            ManagedWorldSessionMode.Local,
            phase,
            new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero));
}
