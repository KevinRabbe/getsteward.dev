using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorkspaceCleanupRecoveryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "steward-cleanup-recovery-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExistingWorkspaceIsDiscardedUsingJournaledExactEnvironment()
    {
        Directory.CreateDirectory(_root);
        var fixture = CreateFixture(WorkspaceRecoveryStatus.CleanupPending, workspaceExists: true);
        var service = new WorkspaceCleanupRecoveryService(fixture.Storage, fixture.Recovery);

        await service.RetryAsync(
            fixture.World.Id,
            fixture.Adapter,
            fixture.Adapter.Installation);

        Assert.Equal(1, fixture.Adapter.FinalizeCount);
        Assert.Equal(PreparedWorldDisposition.Discard, fixture.Adapter.LastDisposition);
        Assert.False(Directory.Exists(fixture.Record.WorkingDirectory));
        Assert.Empty(fixture.Recovery.Records);
        Assert.Equal(0, fixture.Storage.WorldLoadCount);
        Assert.Equal(1, fixture.Storage.EnvironmentLoadCount);
        Assert.Equal(fixture.Record.EnvironmentRevisionId, fixture.Storage.LastEnvironmentRevisionId);
    }

    [Fact]
    public async Task AlreadyMissingWorkspaceRemovesOnlyTheDurableCleanupRecordWithoutInstallation()
    {
        Directory.CreateDirectory(_root);
        var fixture = CreateFixture(WorkspaceRecoveryStatus.CleanupPending, workspaceExists: false);
        fixture.Storage.ThrowIfWorldLoaded = true;
        var service = new WorkspaceCleanupRecoveryService(fixture.Storage, fixture.Recovery);

        await service.RetryAsync(
            fixture.World.Id,
            fixture.Adapter,
            installation: null);

        Assert.Equal(0, fixture.Adapter.FinalizeCount);
        Assert.Equal(0, fixture.Storage.WorldLoadCount);
        Assert.Equal(0, fixture.Storage.EnvironmentLoadCount);
        Assert.Empty(fixture.Recovery.Records);
    }

    [Fact]
    public async Task RecoveryPendingRecordIsNeverConsumedAsCleanupOnlyWork()
    {
        Directory.CreateDirectory(_root);
        var fixture = CreateFixture(WorkspaceRecoveryStatus.RecoveryPending, workspaceExists: true);
        var service = new WorkspaceCleanupRecoveryService(fixture.Storage, fixture.Recovery);

        var exception = await Assert.ThrowsAsync<WorkspaceCleanupRecoveryException>(() =>
            service.RetryAsync(
                fixture.World.Id,
                fixture.Adapter,
                fixture.Adapter.Installation));

        Assert.Equal("CleanupNotFound", exception.Code);
        Assert.Equal(0, fixture.Adapter.FinalizeCount);
        Assert.Single(fixture.Recovery.Records);
        Assert.True(Directory.Exists(fixture.Record.WorkingDirectory));
    }

    [Fact]
    public async Task AdapterCleanupFailurePreservesCleanupRecordAndWorkspace()
    {
        Directory.CreateDirectory(_root);
        var fixture = CreateFixture(WorkspaceRecoveryStatus.CleanupPending, workspaceExists: true);
        fixture.Adapter.ThrowOnFinalize = true;
        var service = new WorkspaceCleanupRecoveryService(fixture.Storage, fixture.Recovery);

        await Assert.ThrowsAsync<IOException>(() => service.RetryAsync(
            fixture.World.Id,
            fixture.Adapter,
            fixture.Adapter.Installation));

        Assert.Equal(1, fixture.Adapter.FinalizeCount);
        Assert.Single(fixture.Recovery.Records);
        Assert.True(Directory.Exists(fixture.Record.WorkingDirectory));
    }

    [Fact]
    public async Task LegacyCleanupRecordWithoutExactEnvironmentPreservesExistingWorkspace()
    {
        Directory.CreateDirectory(_root);
        var fixture = CreateFixture(WorkspaceRecoveryStatus.CleanupPending, workspaceExists: true);
        fixture.Recovery.Records[0] = fixture.Record with { EnvironmentRevisionId = null };
        var service = new WorkspaceCleanupRecoveryService(fixture.Storage, fixture.Recovery);

        var exception = await Assert.ThrowsAsync<WorkspaceCleanupRecoveryException>(() =>
            service.RetryAsync(
                fixture.World.Id,
                fixture.Adapter,
                fixture.Adapter.Installation));

        Assert.Equal("EnvironmentUnknown", exception.Code);
        Assert.Equal(0, fixture.Adapter.FinalizeCount);
        Assert.Equal(0, fixture.Storage.EnvironmentLoadCount);
        Assert.Single(fixture.Recovery.Records);
        Assert.True(Directory.Exists(fixture.Record.WorkingDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Fixture CreateFixture(
        WorkspaceRecoveryStatus status,
        bool workspaceExists)
    {
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();
        var user = new UserIdentity("test", "user", "Test User");
        var manifest = new EnvironmentManifest(
            1,
            "test-adapter",
            "1.0.0",
            [],
            new Dictionary<string, string>());
        var world = new World(
            worldId,
            "Cleanup Test World",
            "test-adapter",
            [user],
            environmentId,
            stateId);
        var environment = new EnvironmentRevision(
            environmentId,
            worldId,
            ParentRevisionId: null,
            DateTimeOffset.UtcNow,
            user,
            manifest);
        var workspace = Path.Combine(_root, $"workspace-{Guid.NewGuid():N}");
        if (workspaceExists)
        {
            Directory.CreateDirectory(workspace);
        }

        var record = new WorkspaceRecoveryRecord(
            WorkspaceId.New(),
            worldId,
            stateId,
            "test-adapter",
            workspace,
            user,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            status,
            CandidateStateRevisionId: status == WorkspaceRecoveryStatus.RecoveryPending
                ? RevisionId.New()
                : stateId,
            EnvironmentRevisionId: environmentId);
        var storage = new CleanupStorage(world, environment);
        var recovery = new CleanupRecoveryStore(record);
        var adapter = new CleanupAdapter(_root);
        return new Fixture(world, record, storage, recovery, adapter);
    }

    private sealed record Fixture(
        World World,
        WorkspaceRecoveryRecord Record,
        CleanupStorage Storage,
        CleanupRecoveryStore Recovery,
        CleanupAdapter Adapter);

    private sealed class CleanupStorage : IWorldStorage
    {
        private readonly World _world;
        private readonly EnvironmentRevision _environment;

        public CleanupStorage(World world, EnvironmentRevision environment)
        {
            _world = world;
            _environment = environment;
        }

        public bool ThrowIfWorldLoaded { get; set; }
        public int WorldLoadCount { get; private set; }
        public int EnvironmentLoadCount { get; private set; }
        public RevisionId? LastEnvironmentRevisionId { get; private set; }

        public Task<World?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            WorldLoadCount++;
            if (ThrowIfWorldLoaded)
            {
                throw new InvalidOperationException("World metadata should not be needed when cleanup already happened.");
            }

            return Task.FromResult<World?>(_world.Id == worldId ? _world : null);
        }

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            EnvironmentLoadCount++;
            LastEnvironmentRevisionId = revisionId;
            return Task.FromResult<EnvironmentRevision?>(
                _environment.WorldId == worldId && _environment.Id == revisionId
                    ? _environment
                    : null);
        }

        public Task SaveWorldAsync(World world, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task StoreEnvironmentRevisionAsync(
            EnvironmentRevision revision,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class CleanupRecoveryStore : IWorkspaceRecoveryStore
    {
        public CleanupRecoveryStore(WorkspaceRecoveryRecord record)
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

    private sealed class CleanupAdapter : IGameAdapter
    {
        public CleanupAdapter(string root)
        {
            Installation = new GameInstallation("test-installation", root, "test");
        }

        public string Id => "test-adapter";
        public string DisplayName => "Cleanup Test Adapter";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.None;
        public GameInstallation Installation { get; }
        public bool ThrowOnFinalize { get; set; }
        public int FinalizeCount { get; private set; }
        public PreparedWorldDisposition? LastDisposition { get; private set; }

        public Task FinalizePreparedWorldAsync(
            PreparedWorld world,
            PreparedWorldDisposition disposition,
            CancellationToken cancellationToken = default)
        {
            FinalizeCount++;
            LastDisposition = disposition;
            if (ThrowOnFinalize)
            {
                throw new IOException("Injected cleanup failure.");
            }

            if (Directory.Exists(world.WorkingDirectory))
            {
                Directory.Delete(world.WorkingDirectory, recursive: true);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameInstallation>>([Installation]);

        public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
            GameInstallation installation,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EnvironmentManifest> InspectEnvironmentAsync(
            GameInstallation installation,
            DetectedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CapturedState> CaptureDetectedWorldAsync(
            GameInstallation installation,
            DetectedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PreparedWorld> PrepareEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<CapturedState> CaptureStateAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RestoreStateAsync(
            PreparedWorld world,
            StatePackage state,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GameSessionHandle> LaunchLocalAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GameSessionHandle> LaunchHostAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GameSessionHandle> LaunchClientAsync(
            PreparedWorld world,
            HostConnection host,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task WaitForSessionEndAsync(
            GameSessionHandle session,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
