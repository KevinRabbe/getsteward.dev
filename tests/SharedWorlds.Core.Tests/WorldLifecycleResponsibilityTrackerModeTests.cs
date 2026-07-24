using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorldLifecycleResponsibilityTrackerModeTests
{
    [Fact]
    public void RunningHostedLifecyclePreservesModeForRuntimeControls()
    {
        var tracker = new WorldLifecycleResponsibilityTracker();
        var worldId = WorldId.New();

        tracker.OnPhaseChanged(new WorldLifecyclePhaseChange(
            worldId,
            ManagedWorldSessionMode.Hosted,
            WorldLifecyclePhase.Running,
            DateTimeOffset.UtcNow));

        var snapshot = tracker.Current;
        Assert.Equal(WorldLifecycleResponsibilityKind.ActiveLifecycle, snapshot.Kind);
        Assert.Equal(worldId, snapshot.WorldId);
        Assert.Equal(WorldLifecyclePhase.Running, snapshot.Phase);
        Assert.Equal(ManagedWorldSessionMode.Hosted, snapshot.Mode);
    }

    [Fact]
    public void RecoveryInitializationNeverInventsHostedMode()
    {
        var tracker = new WorldLifecycleResponsibilityTracker();
        var record = new WorkspaceRecoveryRecord(
            Id: WorkspaceId.New(),
            WorldId: WorldId.New(),
            BaseStateRevisionId: RevisionId.New(),
            AdapterId: "palworld",
            WorkingDirectory: "C:\\steward\\workspace",
            StartedBy: new UserIdentity("local", "tester", "Tester"),
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Status: WorkspaceRecoveryStatus.Active,
            EnvironmentRevisionId: RevisionId.New());

        tracker.InitializeFromRecoveryRecords([record]);

        var snapshot = tracker.Current;
        Assert.Equal(WorldLifecycleResponsibilityKind.InterruptedSession, snapshot.Kind);
        Assert.Null(snapshot.Mode);
    }

    [Fact]
    public void CompletedLifecycleClearsMode()
    {
        var tracker = new WorldLifecycleResponsibilityTracker();
        var worldId = WorldId.New();
        tracker.OnPhaseChanged(new WorldLifecyclePhaseChange(
            worldId,
            ManagedWorldSessionMode.Hosted,
            WorldLifecyclePhase.Running,
            DateTimeOffset.UtcNow));

        tracker.OnPhaseChanged(new WorldLifecyclePhaseChange(
            worldId,
            ManagedWorldSessionMode.Hosted,
            WorldLifecyclePhase.Completed,
            DateTimeOffset.UtcNow));

        Assert.Equal(WorldLifecycleResponsibilityKind.None, tracker.Current.Kind);
        Assert.Null(tracker.Current.Mode);
    }
}
