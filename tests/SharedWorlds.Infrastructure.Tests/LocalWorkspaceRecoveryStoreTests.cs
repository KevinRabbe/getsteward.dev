using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class LocalWorkspaceRecoveryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-recovery-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task RecoveryRecord_RoundTripsAndCanBeRemoved()
    {
        var store = new LocalWorkspaceRecoveryStore(_root);
        var now = DateTimeOffset.UtcNow;
        var record = new WorkspaceRecoveryRecord(
            WorkspaceId.New(),
            WorldId.New(),
            RevisionId.New(),
            "factorio",
            Path.Combine(_root, "workspace"),
            new UserIdentity("local", "tester"),
            now,
            now,
            WorkspaceRecoveryStatus.Active);

        await store.SaveAsync(record);
        var records = await store.ListAsync();

        Assert.Equal(record, Assert.Single(records));

        await store.RemoveAsync(record.Id);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task RecoveryRecord_StatusUpdate_ReplacesMutableRegistryEntry()
    {
        var store = new LocalWorkspaceRecoveryStore(_root);
        var now = DateTimeOffset.UtcNow;
        var record = new WorkspaceRecoveryRecord(
            WorkspaceId.New(),
            WorldId.New(),
            RevisionId.New(),
            "factorio",
            Path.Combine(_root, "workspace"),
            new UserIdentity("local", "tester"),
            now,
            now,
            WorkspaceRecoveryStatus.Active);

        await store.SaveAsync(record);
        await store.SaveAsync(record with
        {
            Status = WorkspaceRecoveryStatus.RecoveryPending,
            UpdatedAt = now.AddMinutes(1),
            Reason = "Injected failure"
        });

        var loaded = Assert.Single(await store.ListAsync());
        Assert.Equal(WorkspaceRecoveryStatus.RecoveryPending, loaded.Status);
        Assert.Equal("Injected failure", loaded.Reason);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Test cleanup must not hide the actual assertion result.
        }
        catch (UnauthorizedAccessException)
        {
            // Test cleanup must not hide the actual assertion result.
        }
    }
}
