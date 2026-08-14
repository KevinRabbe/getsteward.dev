using SharedWorlds.Core.Domain;

namespace SharedWorlds.Core.Tests;

public sealed class PreparedWorldRecoveryLocationTests
{
    [Fact]
    public void ManagedLocationContainsNoMachinePathPayload()
    {
        var location = PreparedWorldRecoveryLocation.Managed();

        Assert.Equal(1, location.SchemaVersion);
        Assert.Equal(
            PreparedWorldRecoveryLocationKind.SafeWorldManaged,
            location.Kind);
        Assert.Null(location.NativeIdentity);
        location.Validate();
    }

    [Fact]
    public void NativeLocationCopiesStableAdapterIdentity()
    {
        var identity = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["worldId"] = "native-world-1"
        };

        var location = PreparedWorldRecoveryLocation.Native(identity);
        identity["worldId"] = "mutated-after-create";

        Assert.Equal(1, location.SchemaVersion);
        Assert.Equal(
            PreparedWorldRecoveryLocationKind.NativeGame,
            location.Kind);
        Assert.Equal("native-world-1", location.NativeIdentity!["worldId"]);
        location.Validate();
    }

    [Fact]
    public void NativeLocationRejectsEmptyIdentity()
    {
        Assert.Throws<ArgumentException>(() =>
            PreparedWorldRecoveryLocation.Native(
                new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    [Fact]
    public void ValidateRejectsUnknownSchemaVersion()
    {
        var location = PreparedWorldRecoveryLocation.Managed() with
        {
            SchemaVersion = 999
        };

        Assert.Throws<InvalidDataException>(location.Validate);
    }

    [Fact]
    public void LegacyWorkspaceRecoveryRecordCanExistWithoutNewLocationDescriptor()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new WorkspaceRecoveryRecord(
            WorkspaceId.New(),
            WorldId.New(),
            RevisionId.New(),
            "adapter",
            "legacy-absolute-runtime-path",
            new UserIdentity("test", "user"),
            now,
            now,
            WorkspaceRecoveryStatus.RecoveryPending);

        Assert.Null(record.RecoveryLocation);
    }
}
