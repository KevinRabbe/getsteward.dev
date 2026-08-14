using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Storage;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class PreparationPendingRecoveryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "safeworld-preparation-pending-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MissingManagedRuntimeClearsOnlyPreparationJournal()
    {
        var managed = new ManagedWorkspaceStorage(Path.Combine(_root, "managed"));
        var record = CreateRecord(PreparedWorldRecoveryLocation.Managed());
        var recovery = new RecoveryStore(record);
        var service = new PreparationPendingRecoveryService(recovery, managed);

        await service.ResolveManagedAsync(record.Id);

        Assert.Empty(recovery.Records);
    }

    [Fact]
    public async Task ExistingManagedRuntimeIsDiscardedByExactWorkspaceIdentity()
    {
        var managed = new ManagedWorkspaceStorage(Path.Combine(_root, "managed"));
        var record = CreateRecord(PreparedWorldRecoveryLocation.Managed()) with
        {
            WorkingDirectory = Path.Combine(_root, "wrong-legacy-path")
        };
        var managedPath = managed.Create(record.Id, record.AdapterId);
        await File.WriteAllTextAsync(Path.Combine(managedPath, "partial.txt"), "partial");
        var recovery = new RecoveryStore(record);
        var service = new PreparationPendingRecoveryService(recovery, managed);

        await service.ResolveManagedAsync(record.Id);

        Assert.False(Directory.Exists(managedPath));
        Assert.Empty(recovery.Records);
    }

    [Fact]
    public async Task ExactWorkspaceIdentityCannotSelectSiblingPendingRecordForSameWorld()
    {
        var managed = new ManagedWorkspaceStorage(Path.Combine(_root, "managed"));
        var worldId = WorldId.New();
        var managedRecord = CreateRecord(PreparedWorldRecoveryLocation.Managed()) with
        {
            WorldId = worldId
        };
        var nativeRecord = CreateRecord(
            PreparedWorldRecoveryLocation.Native(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["worldId"] = "NATIVE-KEEP"
                })) with
        {
            WorldId = worldId
        };
        var managedPath = managed.Create(managedRecord.Id, managedRecord.AdapterId);
        await File.WriteAllTextAsync(Path.Combine(managedPath, "partial.txt"), "partial");
        var recovery = new RecoveryStore(nativeRecord, managedRecord);
        var service = new PreparationPendingRecoveryService(recovery, managed);

        await service.ResolveManagedAsync(managedRecord.Id);

        Assert.False(Directory.Exists(managedPath));
        Assert.Equal(nativeRecord, Assert.Single(recovery.Records));
    }

    [Fact]
    public async Task NativeIdentityIsNeverAutomaticallyDiscarded()
    {
        var managed = new ManagedWorkspaceStorage(Path.Combine(_root, "managed"));
        var record = CreateRecord(
            PreparedWorldRecoveryLocation.Native(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["worldId"] = "ABC123"
                }));
        var recovery = new RecoveryStore(record);
        var service = new PreparationPendingRecoveryService(recovery, managed);

        var exception = await Assert.ThrowsAsync<PreparationPendingRecoveryException>(() =>
            service.ResolveManagedAsync(record.Id));

        Assert.Equal("NativeResolutionRequired", exception.Code);
        Assert.Equal(record, Assert.Single(recovery.Records));
    }

    [Fact]
    public async Task MissingRecoveryIdentityFailsClosedAndPreservesJournal()
    {
        var managed = new ManagedWorkspaceStorage(Path.Combine(_root, "managed"));
        var record = CreateRecord(location: null);
        var recovery = new RecoveryStore(record);
        var service = new PreparationPendingRecoveryService(recovery, managed);

        var exception = await Assert.ThrowsAsync<PreparationPendingRecoveryException>(() =>
            service.ResolveManagedAsync(record.Id));

        Assert.Equal("RecoveryIdentityMissing", exception.Code);
        Assert.Single(recovery.Records);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static WorkspaceRecoveryRecord CreateRecord(PreparedWorldRecoveryLocation? location)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRecoveryRecord(
            WorkspaceId.New(),
            WorldId.New(),
            RevisionId.New(),
            "test-adapter",
            string.Empty,
            new UserIdentity("test", "user", "Test User"),
            now,
            now,
            WorkspaceRecoveryStatus.PreparationPending,
            EnvironmentRevisionId: RevisionId.New(),
            RecoveryLocation: location);
    }

    private sealed class RecoveryStore : IWorkspaceRecoveryStore
    {
        public RecoveryStore(params WorkspaceRecoveryRecord[] records)
        {
            Records.AddRange(records);
        }

        public List<WorkspaceRecoveryRecord> Records { get; } = [];

        public Task SaveAsync(WorkspaceRecoveryRecord record, CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(existing => existing.Id == record.Id);
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(WorkspaceId workspaceId, CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(record => record.Id == workspaceId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkspaceRecoveryRecord>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRecoveryRecord>>(Records.ToArray());
    }
}
