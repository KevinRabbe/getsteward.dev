using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorldLifecycleResponsibilityAbandonmentTests
{
    [Fact]
    public void AbandonedEvidenceDoesNotOwnRuntimeResponsibility()
    {
        var tracker = new WorldLifecycleResponsibilityTracker();
        tracker.InitializeFromRecoveryRecords([
            CreateRecord(WorkspaceRecoveryStatus.Abandoned, DateTimeOffset.UtcNow)
        ]);

        var snapshot = tracker.Current;
        Assert.Equal(WorldLifecycleResponsibilityKind.None, snapshot.Kind);
        Assert.Null(snapshot.WorldId);
        Assert.True(snapshot.CanQuitWithoutGuard);
        Assert.True(snapshot.CanSelfUpdate);
    }

    [Fact]
    public void ActiveEvidenceStillWinsWhenAbandonedEvidenceAlsoExists()
    {
        var active = CreateRecord(
            WorkspaceRecoveryStatus.Active,
            DateTimeOffset.UtcNow.AddMinutes(-1));
        var tracker = new WorldLifecycleResponsibilityTracker();
        tracker.InitializeFromRecoveryRecords([
            CreateRecord(WorkspaceRecoveryStatus.Abandoned, DateTimeOffset.UtcNow),
            active
        ]);

        var snapshot = tracker.Current;
        Assert.Equal(WorldLifecycleResponsibilityKind.InterruptedSession, snapshot.Kind);
        Assert.Equal(active.WorldId, snapshot.WorldId);
        Assert.False(snapshot.CanQuitWithoutGuard);
    }

    private static WorkspaceRecoveryRecord CreateRecord(
        WorkspaceRecoveryStatus status,
        DateTimeOffset updatedAt)
        => new(
            WorkspaceId.New(),
            WorldId.New(),
            RevisionId.New(),
            "test-adapter",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            new UserIdentity("test", "user", "Test User"),
            updatedAt.AddMinutes(-1),
            updatedAt,
            status);
}
