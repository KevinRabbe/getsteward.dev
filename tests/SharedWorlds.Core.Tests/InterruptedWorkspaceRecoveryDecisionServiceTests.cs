using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
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
    public async Task DiscardDecisionTransitionsManagedExactEnvironmentToCleanupWithoutPathProbe()
    {
        var record = CreateRecord(workspaceExists: true);
        var store = new RecoveryStore(record);
        var service = new InterruptedWorkspaceRecoveryDecisionService(store);

        var updated = await service.PrepareDiscardAsync(
            record.WorldId,
            new DecisionAdapter(record.AdapterId));

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
    public async Task DescriptorBasedNativeRecordCannotUseGenericDiscard()
    {
        var record = CreateRecord(workspaceExists: false) with
        {
            WorkingDirectory = string.Empty,
            RecoveryLocation = PreparedWorldRecoveryLocation.Native(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["worldId"] = "ABC123"
                })
        };
        var store = new RecoveryStore(record);
        var service = new InterruptedWorkspaceRecoveryDecisionService(store);

        var exception = await Assert.ThrowsAsync<InterruptedWorkspaceRecoveryDecisionException>(() =>
            service.PrepareDiscardAsync(
                record.WorldId,
                new NativeDecisionAdapter(record.AdapterId)));

        Assert.Equal("NativeDiscardUnsupported", exception.Code);
        Assert.Equal(WorkspaceRecoveryStatus.Active, Assert.Single(store.Records).Status);
    }

    [Fact]
    public async Task LegacyRecordFromNativeRecoveryAdapterCannotUseGenericDiscard()
    {
        var record = CreateRecord(workspaceExists: true);
        var store = new RecoveryStore(record);
        var service = new InterruptedWorkspaceRecoveryDecisionService(store);

        var exception = await Assert.ThrowsAsync<InterruptedWorkspaceRecoveryDecisionException>(() =>
            service.PrepareDiscardAsync(
                record.WorldId,
                new NativeDecisionAdapter(record.AdapterId)));

        Assert.Equal("NativeDiscardUnsupported", exception.Code);
        Assert.Equal(WorkspaceRecoveryStatus.Active, Assert.Single(store.Records).Status);
        Assert.True(Directory.Exists(record.WorkingDirectory));
    }

    [Fact]
    public async Task LegacyManagedRecordWithoutEnvironmentBecomesAbandonedEvidence()
    {
        var record = CreateRecord(workspaceExists: true) with { EnvironmentRevisionId = null };
        var store = new RecoveryStore(record);
        var service = new InterruptedWorkspaceRecoveryDecisionService(store);

        var updated = await service.PrepareDiscardAsync(
            record.WorldId,
            new DecisionAdapter(record.AdapterId));

        Assert.Equal(WorkspaceRecoveryStatus.Abandoned, updated.Status);
        Assert.Contains("abandoned", updated.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(updated, Assert.Single(store.Records));
        Assert.True(Directory.Exists(record.WorkingDirectory));
    }

    [Fact]
    public async Task MissingLegacyManagedPathWithoutEnvironmentStillBecomesAbandonedEvidence()
    {
        var record = CreateRecord(workspaceExists: false) with { EnvironmentRevisionId = null };
        var store = new RecoveryStore(record);
        var service = new InterruptedWorkspaceRecoveryDecisionService(store);

        var updated = await service.PrepareDiscardAsync(
            record.WorldId,
            new DecisionAdapter(record.AdapterId));

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

    private class DecisionAdapter : IGameAdapter
    {
        public DecisionAdapter(string id)
        {
            Id = id;
        }

        public string Id { get; }
        public string DisplayName => "Decision Adapter";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.None;

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(GameInstallation installation, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EnvironmentManifest> InspectEnvironmentAsync(GameInstallation installation, DetectedWorld world, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CapturedState> CaptureDetectedWorldAsync(GameInstallation installation, DetectedWorld world, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PreparedWorld> PrepareEnvironmentAsync(GameInstallation installation, EnvironmentManifest requiredEnvironment, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CapturedState> CaptureStateAsync(PreparedWorld world, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RestoreStateAsync(PreparedWorld world, StatePackage state, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task FinalizePreparedWorldAsync(PreparedWorld world, PreparedWorldDisposition disposition, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class NativeDecisionAdapter : DecisionAdapter, INativePreparedWorldRecoveryAdapter
    {
        public NativeDecisionAdapter(string id)
            : base(id)
        {
        }

        public PreparedWorld ResolveNativePreparedWorld(
            GameInstallation installation,
            EnvironmentManifest environment,
            PreparedWorldRecoveryLocation recoveryLocation,
            string? displayName = null)
            => throw new NotSupportedException();
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
