using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class InterruptedWorkspaceRecoveryDecisionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "steward-interrupted-recovery-decision-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RecoverDecisionCreatesStableCandidateAndRecoveryPendingRecord()
    {
        var record = CreateRecord(workspaceExists: true);
        var store = new RecoveryStore(record);
        var service = new InterruptedWorkspaceRecoveryDecisionService(store);

        var updated = await service.PrepareRecoveryAsync(record.WorldId, record.AdapterId);

        Assert.Equal(WorkspaceRecoveryStatus.RecoveryPending, updated.Status);
        Assert.NotNull(updated.CandidateStateRevisionId);
        Assert.Equal(record.EnvironmentRevisionId, updated.EnvironmentRevisionId);
        Assert.Equal(updated, Assert.Single(store.Records));
        Assert.True(Directory.Exists(record.WorkingDirectory));
    }

    [Fact]
    public async Task RecoverDecisionReusesCandidateAlreadyJournaledBeforeCrash()
    {
        var existingCandidate = RevisionId.New();
        var record = CreateRecord(workspaceExists: true) with
        {
            CandidateStateRevisionId = existingCandidate
        };
        var store = new RecoveryStore(record);
        var service = new InterruptedWorkspaceRecoveryDecisionService(store);

        var updated = await service.PrepareRecoveryAsync(record.WorldId, record.AdapterId);

        Assert.Equal(existingCandidate, updated.CandidateStateRevisionId);
    }

    [Fact]
    public async Task DiscardDecisionTransitionsExactEnvironmentToCleanupWithoutPathProbe()
    {
        var record = CreateRecord(workspaceExists: true);
        var store = new RecoveryStore(record);
        var service = new InterruptedWorkspaceRecoveryDecisionService(store);

        var updated = await service.PrepareDiscardAsync(record.WorldId, record.AdapterId);

        Assert.Equal(WorkspaceRecoveryStatus.CleanupPending, updated.Status);
        Assert.True(Directory.Exists(record.WorkingDirectory));
        Assert.Equal(updated, Assert.Single(store.Records));
    }

    [Fact]
    public async Task RecoverDecisionDoesNotUsePersistedPathAsAvailabilityAuthority()
    {
        var record = CreateRecord(workspaceExists: false);
        var store = new RecoveryStore(record);
        var service = new InterruptedWorkspaceRecoveryDecisionService(store);

        var updated = await service.PrepareRecoveryAsync(record.WorldId, record.AdapterId);

        Assert.Equal(WorkspaceRecoveryStatus.RecoveryPending, updated.Status);
        Assert.NotNull(updated.CandidateStateRevisionId);
    }

    [Fact]
    public async Task DescriptorBasedPathFreeRecordCanChooseRecovery()
    {
        var record = CreateRecord(workspaceExists: false) with
        {
            WorkingDirectory = string.Empty,
            RecoveryLocation = PreparedWorldRecoveryLocation.Managed()
        };
        var store = new RecoveryStore(record);
        var service = new InterruptedWorkspaceRecoveryDecisionService(store);

        var updated = await service.PrepareRecoveryAsync(record.WorldId, record.AdapterId);

        Assert.Equal(WorkspaceRecoveryStatus.RecoveryPending, updated.Status);
        Assert.Equal(PreparedWorldRecoveryLocationKind.SafeWorldManaged, updated.RecoveryLocation!.Kind);
    }

    [Fact]
    public async Task LegacyRecordWithoutEnvironmentBecomesAbandonedEvidence()
    {
        var record = CreateRecord(workspaceExists: true) with { EnvironmentRevisionId = null };
        var store = new RecoveryStore(record);
        var service = new InterruptedWorkspaceRecoveryDecisionService(store);

        var updated = await service.PrepareDiscardAsync(record.WorldId, record.AdapterId);

        Assert.Equal(WorkspaceRecoveryStatus.Abandoned, updated.Status);
        Assert.Contains("abandoned", updated.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(updated, Assert.Single(store.Records));
        Assert.True(Directory.Exists(record.WorkingDirectory));
    }

    [Fact]
    public async Task MissingLegacyPathWithoutEnvironmentStillBecomesAbandonedEvidence()
    {
        var record = CreateRecord(workspaceExists: false) with { EnvironmentRevisionId = null };
        var store = new RecoveryStore(record);
        var service = new InterruptedWorkspaceRecoveryDecisionService(store);

        var updated = await service.PrepareDiscardAsync(record.WorldId, record.AdapterId);

        Assert.Equal(WorkspaceRecoveryStatus.Abandoned, updated.Status);
    }

    [Fact]
    public async Task RecoveryWithoutExactEnvironmentFailsClosedBeforePathInspection()
    {
        var record = CreateRecord(workspaceExists: false) with
        {
            EnvironmentRevisionId = null,
            WorkingDirectory = string.Empty
        };
        var store = new RecoveryStore(record);
        var service = new InterruptedWorkspaceRecoveryDecisionService(store);

        var exception = await Assert.ThrowsAsync<InterruptedWorkspaceRecoveryDecisionException>(() =>
            service.PrepareRecoveryAsync(record.WorldId, record.AdapterId));

        Assert.Equal("EnvironmentUnknown", exception.Code);
        Assert.Equal(WorkspaceRecoveryStatus.Active, Assert.Single(store.Records).Status);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private WorkspaceRecoveryRecord CreateRecord(bool workspaceExists)
    {
        var workspace = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        if (workspaceExists)
        {
            Directory.CreateDirectory(workspace);
        }

        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRecoveryRecord(
            WorkspaceId.New(),
            WorldId.New(),
            RevisionId.New(),
            "test-adapter",
            workspace,
            new UserIdentity("test", "user", "Test User"),
            now,
            now,
            WorkspaceRecoveryStatus.Active,
            EnvironmentRevisionId: RevisionId.New());
    }

    private sealed class RecoveryStore : IWorkspaceRecoveryStore
    {
        public RecoveryStore(WorkspaceRecoveryRecord record)
        {
            Records.Add(record);
        }

        public List<WorkspaceRecoveryRecord> Records { get; } = [];

        public Task SaveAsync(
            WorkspaceRecoveryRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(existing => existing.Id == record.Id);
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            WorkspaceId workspaceId,
            CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(record => record.Id == workspaceId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkspaceRecoveryRecord>> ListAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRecoveryRecord>>(Records.ToArray());
    }
}
