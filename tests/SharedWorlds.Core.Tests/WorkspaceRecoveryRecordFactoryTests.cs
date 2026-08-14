using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorkspaceRecoveryRecordFactoryTests
{
    [Fact]
    public void PlannedRecordPersistsStableIdentityButNoAbsoluteRuntimePath()
    {
        var workspaceId = WorkspaceId.New();
        var location = PreparedWorldRecoveryLocation.Native(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["worldId"] = "ABC123"
            });
        var now = DateTimeOffset.UtcNow;

        var record = WorkspaceRecoveryRecordFactory.CreatePlanned(
            workspaceId,
            WorldId.New(),
            RevisionId.New(),
            RevisionId.New(),
            "palworld",
            new UserIdentity("local", "user", "User"),
            location,
            now);

        Assert.Equal(workspaceId, record.Id);
        Assert.Equal(string.Empty, record.WorkingDirectory);
        Assert.Equal(location, record.RecoveryLocation);
        Assert.Equal(WorkspaceRecoveryStatus.CleanupPending, record.Status);
        Assert.Equal(now, record.CreatedAt);
        Assert.Equal(now, record.UpdatedAt);
    }

    [Fact]
    public void ManagedPlanAlsoAvoidsPersistingCurrentMachinePath()
    {
        var record = WorkspaceRecoveryRecordFactory.CreatePlanned(
            WorkspaceId.New(),
            WorldId.New(),
            RevisionId.New(),
            RevisionId.New(),
            "factorio",
            new UserIdentity("local", "user", "User"),
            PreparedWorldRecoveryLocation.Managed(),
            DateTimeOffset.UtcNow);

        Assert.Empty(record.WorkingDirectory);
        Assert.Equal(
            PreparedWorldRecoveryLocationKind.SafeWorldManaged,
            record.RecoveryLocation!.Kind);
    }
}
