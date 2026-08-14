using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Storage;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class PreparedWorldMaterializationCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "safeworld-materialization-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RecoveryIdentityIsDurableBeforeAdapterMaterializesManagedRuntime()
    {
        var recovery = new RecoveryStore();
        var managed = new ManagedWorkspaceStorage(Path.Combine(_root, "managed"));
        var coordinator = new PreparedWorldMaterializationCoordinator(recovery, managed);
        var adapter = new TestAdapter(recovery, materializationKind: MaterializationKind.Managed);
        var environment = CreateEnvironment(adapter.Id);

        var result = await coordinator.MaterializeAsync(
            WorldId.New(),
            RevisionId.New(),
            RevisionId.New(),
            adapter,
            adapter.Installation,
            environment,
            TestUser());

        Assert.Equal(WorkspaceRecoveryStatus.PreparationPending, adapter.ObservedStatusBeforeMaterialization);
        Assert.Equal(string.Empty, adapter.ObservedWorkingDirectoryBeforeMaterialization);
        Assert.Equal(
            PreparedWorldRecoveryLocationKind.SafeWorldManaged,
            adapter.ObservedLocationBeforeMaterialization?.Kind);
        Assert.Equal(result.PreparationContext.ManagedWorkingDirectory, result.PreparedWorld.WorkingDirectory);
        Assert.Equal(WorkspaceRecoveryStatus.CleanupPending, result.RecoveryRecord.Status);
        Assert.Equal(string.Empty, result.RecoveryRecord.WorkingDirectory);
        Assert.Equal(result.RecoveryRecord, Assert.Single(recovery.Records));
    }

    [Fact]
    public async Task ActivePromotionOccursOnlyAfterExplicitReadyForLaunchBoundary()
    {
        var recovery = new RecoveryStore();
        var coordinator = new PreparedWorldMaterializationCoordinator(
            recovery,
            new ManagedWorkspaceStorage(Path.Combine(_root, "managed")));
        var adapter = new TestAdapter(recovery, MaterializationKind.Managed);
        var result = await coordinator.MaterializeAsync(
            WorldId.New(),
            RevisionId.New(),
            RevisionId.New(),
            adapter,
            adapter.Installation,
            CreateEnvironment(adapter.Id),
            TestUser());

        Assert.Equal(WorkspaceRecoveryStatus.CleanupPending, Assert.Single(recovery.Records).Status);

        var active = await coordinator.MarkReadyForLaunchAsync(result.RecoveryRecord);

        Assert.Equal(WorkspaceRecoveryStatus.Active, active.Status);
        Assert.Equal(active, Assert.Single(recovery.Records));
    }

    [Fact]
    public async Task PreparationFailureLeavesConservativePreparationPendingJournal()
    {
        var recovery = new RecoveryStore();
        var coordinator = new PreparedWorldMaterializationCoordinator(
            recovery,
            new ManagedWorkspaceStorage(Path.Combine(_root, "managed")));
        var adapter = new TestAdapter(recovery, MaterializationKind.ThrowDuringPreparation);

        await Assert.ThrowsAsync<IOException>(() => coordinator.MaterializeAsync(
            WorldId.New(),
            RevisionId.New(),
            RevisionId.New(),
            adapter,
            adapter.Installation,
            CreateEnvironment(adapter.Id),
            TestUser()));

        var record = Assert.Single(recovery.Records);
        Assert.Equal(WorkspaceRecoveryStatus.PreparationPending, record.Status);
        Assert.Equal(string.Empty, record.WorkingDirectory);
    }

    [Fact]
    public async Task MaterializedIdentityMismatchLeavesPreflightJournalAndRefusesPromotion()
    {
        var recovery = new RecoveryStore();
        var coordinator = new PreparedWorldMaterializationCoordinator(
            recovery,
            new ManagedWorkspaceStorage(Path.Combine(_root, "managed")));
        var adapter = new TestAdapter(recovery, MaterializationKind.WrongManagedPath);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.MaterializeAsync(
            WorldId.New(),
            RevisionId.New(),
            RevisionId.New(),
            adapter,
            adapter.Installation,
            CreateEnvironment(adapter.Id),
            TestUser()));

        Assert.Equal(
            WorkspaceRecoveryStatus.PreparationPending,
            Assert.Single(recovery.Records).Status);
    }

    [Fact]
    public async Task PlanningFailureCreatesNoJournalBecausePlannerMustBeSideEffectFree()
    {
        var recovery = new RecoveryStore();
        var coordinator = new PreparedWorldMaterializationCoordinator(
            recovery,
            new ManagedWorkspaceStorage(Path.Combine(_root, "managed")));
        var adapter = new TestAdapter(recovery, MaterializationKind.PlanningFailure);

        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.MaterializeAsync(
            WorldId.New(),
            RevisionId.New(),
            RevisionId.New(),
            adapter,
            adapter.Installation,
            CreateEnvironment(adapter.Id),
            TestUser()));

        Assert.Empty(recovery.Records);
    }

    [Fact]
    public async Task LegacyAdapterCannotEnterNewMaterializationPath()
    {
        var recovery = new RecoveryStore();
        var coordinator = new PreparedWorldMaterializationCoordinator(
            recovery,
            new ManagedWorkspaceStorage(Path.Combine(_root, "managed")));
        var adapter = new LegacyAdapter();

        await Assert.ThrowsAsync<NotSupportedException>(() => coordinator.MaterializeAsync(
            WorldId.New(),
            RevisionId.New(),
            RevisionId.New(),
            adapter,
            adapter.Installation,
            CreateEnvironment(adapter.Id),
            TestUser()));

        Assert.Empty(recovery.Records);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static UserIdentity TestUser()
        => new("test", "user", "Test User");

    private static EnvironmentManifest CreateEnvironment(string adapterId)
        => new(
            1,
            adapterId,
            "1.0",
            [],
            new Dictionary<string, string>());

    private enum MaterializationKind
    {
        Managed,
        WrongManagedPath,
        ThrowDuringPreparation,
        PlanningFailure
    }

    private sealed class TestAdapter : IGameAdapter, IPreparedWorldRecoveryPlanner
    {
        private readonly RecoveryStore _recovery;
        private readonly MaterializationKind _kind;

        public TestAdapter(RecoveryStore recovery, MaterializationKind materializationKind)
        {
            _recovery = recovery;
            _kind = materializationKind;
            Installation = new GameInstallation("test", Path.GetTempPath(), "test");
        }

        public string Id => "test-adapter";
        public string DisplayName => "Test Adapter";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.None;
        public GameInstallation Installation { get; }
        public WorkspaceRecoveryStatus? ObservedStatusBeforeMaterialization { get; private set; }
        public string? ObservedWorkingDirectoryBeforeMaterialization { get; private set; }
        public PreparedWorldRecoveryLocation? ObservedLocationBeforeMaterialization { get; private set; }

        public Task<PreparedWorldRecoveryLocation> PlanPreparedWorldRecoveryAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            PreparedWorldPreparationContext preparation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_kind == MaterializationKind.PlanningFailure)
            {
                throw new InvalidDataException("Injected planning failure.");
            }

            return Task.FromResult(PreparedWorldRecoveryLocation.Managed());
        }

        public Task<PreparedWorld> PrepareEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            PreparedWorldPreparationContext preparation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = Assert.Single(_recovery.Records);
            ObservedStatusBeforeMaterialization = record.Status;
            ObservedWorkingDirectoryBeforeMaterialization = record.WorkingDirectory;
            ObservedLocationBeforeMaterialization = record.RecoveryLocation;

            if (_kind == MaterializationKind.ThrowDuringPreparation)
            {
                Directory.CreateDirectory(preparation.ManagedWorkingDirectory);
                throw new IOException("Injected materialization failure.");
            }

            var workingDirectory = _kind == MaterializationKind.WrongManagedPath
                ? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
                : preparation.ManagedWorkingDirectory;
            Directory.CreateDirectory(workingDirectory);
            return Task.FromResult(new PreparedWorld(
                installation,
                workingDirectory,
                requiredEnvironment,
                RecoveryLocation: PreparedWorldRecoveryLocation.Managed()));
        }

        public Task<PreparedWorld> PrepareEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The new coordinator must use context-aware preparation.");

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameInstallation>>([Installation]);

        public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(GameInstallation installation, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EnvironmentManifest> InspectEnvironmentAsync(GameInstallation installation, DetectedWorld world, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CapturedState> CaptureDetectedWorldAsync(GameInstallation installation, DetectedWorld world, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CapturedState> CaptureStateAsync(PreparedWorld world, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RestoreStateAsync(PreparedWorld world, StatePackage state, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task FinalizePreparedWorldAsync(PreparedWorld world, PreparedWorldDisposition disposition, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class LegacyAdapter : IGameAdapter
    {
        public string Id => "legacy-adapter";
        public string DisplayName => "Legacy Adapter";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.None;
        public GameInstallation Installation { get; } = new("legacy", Path.GetTempPath(), "test");

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameInstallation>>([Installation]);

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
            => Task.CompletedTask;
    }

    private sealed class RecoveryStore : IWorkspaceRecoveryStore
    {
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
