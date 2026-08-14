using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Core.Storage;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorldLifecycleRecoveryPlanningIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "safeworld-lifecycle-recovery-planning",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PreparationFailureLeavesIdentityJournalReleasesAuthorityAndBlocksNextWritableAttempt()
    {
        Directory.CreateDirectory(_root);
        var user = new UserIdentity("test", "user", "Test User");
        var adapter = new FailingPlannerAdapter();
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();
        var world = new World(
            worldId,
            "Planner crash test",
            adapter.Id,
            [user],
            environmentId,
            stateId)
        {
            SharingMode = WorldSharingMode.Shared
        };
        var environment = new EnvironmentRevision(
            environmentId,
            worldId,
            ParentRevisionId: null,
            DateTimeOffset.UtcNow,
            user,
            adapter.Manifest);
        var state = new StateRevision(
            stateId,
            worldId,
            ParentRevisionId: null,
            DateTimeOffset.UtcNow,
            user,
            adapter.Id,
            "seed",
            EnvironmentRevisionId: environmentId);
        var storage = new FixtureStorage(world, environment, state);
        var sessions = new RecordingSessionCoordinator();
        var recovery = new RecordingRecoveryStore();
        adapter.Recovery = recovery;
        var managed = new ManagedWorkspaceStorage(Path.Combine(_root, "managed"));
        var lifecycle = new WorldLifecycleService(
            storage,
            sessions,
            recovery,
            new ManagedWritableSessionGate(),
            NullWorldLifecycleObserver.Instance,
            managed);

        await Assert.ThrowsAsync<IOException>(() => lifecycle.ContinueAsHostAsync(
            worldId,
            adapter,
            adapter.Installation,
            user));

        Assert.Equal(1, sessions.AcquireCount);
        Assert.Equal(1, sessions.ReleaseCount);
        Assert.Equal(WorkspaceRecoveryStatus.PreparationPending, adapter.ObservedStatusBeforeFailure);
        Assert.Equal(string.Empty, adapter.ObservedWorkingDirectoryBeforeFailure);
        var pending = Assert.Single(recovery.Records.Values);
        Assert.Equal(WorkspaceRecoveryStatus.PreparationPending, pending.Status);
        Assert.Equal(PreparedWorldRecoveryLocationKind.SafeWorldManaged, pending.RecoveryLocation!.Kind);
        Assert.True(Directory.Exists(managed.GetWorkspaceDirectory(pending.Id, adapter.Id)));

        var second = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifecycle.ContinueAsHostAsync(
                worldId,
                adapter,
                adapter.Installation,
                user));

        Assert.Contains("PreparationPending", second.Message, StringComparison.Ordinal);
        Assert.Equal(1, sessions.AcquireCount);
        Assert.Equal(1, sessions.ReleaseCount);
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
        catch
        {
            // Test cleanup only.
        }
    }

    private sealed class FailingPlannerAdapter : IGameAdapter, IPreparedWorldRecoveryPlanner
    {
        public string Id => "planner-integration";
        public string DisplayName => "Planner integration";
        public GameAdapterCapabilities Capabilities =>
            GameAdapterCapabilities.AutomaticHostLaunch |
            GameAdapterCapabilities.ExactGameVersion;
        public GameInstallation Installation { get; } = new("test", Path.GetTempPath(), "test");
        public EnvironmentManifest Manifest => new(
            1,
            Id,
            "1.0",
            [],
            new Dictionary<string, string>());
        public RecordingRecoveryStore? Recovery { get; set; }
        public WorkspaceRecoveryStatus? ObservedStatusBeforeFailure { get; private set; }
        public string? ObservedWorkingDirectoryBeforeFailure { get; private set; }

        public Task<PreparedWorldRecoveryLocation> PlanPreparedWorldRecoveryAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            PreparedWorldPreparationContext preparation,
            CancellationToken cancellationToken = default)
            => Task.FromResult(PreparedWorldRecoveryLocation.Managed());

        public Task<PreparedWorld> PrepareEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            PreparedWorldPreparationContext preparation,
            CancellationToken cancellationToken = default)
        {
            var record = Assert.Single(Recovery!.Records.Values);
            ObservedStatusBeforeFailure = record.Status;
            ObservedWorkingDirectoryBeforeFailure = record.WorkingDirectory;
            Directory.CreateDirectory(preparation.ManagedWorkingDirectory);
            File.WriteAllText(
                Path.Combine(preparation.ManagedWorkingDirectory, "partial.txt"),
                "partial");
            throw new IOException("Injected crash during planner-capable materialization.");
        }

        public Task<EnvironmentVerificationReport> VerifyEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            CancellationToken cancellationToken = default)
            => Task.FromResult(EnvironmentVerificationReport.Ready());

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameInstallation>>([Installation]);

        public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(GameInstallation installation, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DetectedWorld>>([]);

        public Task<EnvironmentManifest> InspectEnvironmentAsync(GameInstallation installation, DetectedWorld world, CancellationToken cancellationToken = default)
            => Task.FromResult(Manifest);

        public Task<CapturedState> CaptureDetectedWorldAsync(GameInstallation installation, DetectedWorld world, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PreparedWorld> PrepareEnvironmentAsync(GameInstallation installation, EnvironmentManifest requiredEnvironment, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Legacy preparation must not be used for a planner-capable writable session.");

        public Task<CapturedState> CaptureStateAsync(PreparedWorld world, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RestoreStateAsync(PreparedWorld world, StatePackage state, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task FinalizePreparedWorldAsync(PreparedWorld world, PreparedWorldDisposition disposition, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FixtureStorage : IWorldStorage
    {
        private readonly World _world;
        private readonly EnvironmentRevision _environment;
        private readonly StateRevision _state;

        public FixtureStorage(World world, EnvironmentRevision environment, StateRevision state)
        {
            _world = world;
            _environment = environment;
            _state = state;
        }

        public Task<World?> LoadWorldAsync(WorldId worldId, CancellationToken cancellationToken = default)
            => Task.FromResult<World?>(worldId == _world.Id ? _world : null);

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(WorldId worldId, RevisionId revisionId, CancellationToken cancellationToken = default)
            => Task.FromResult<EnvironmentRevision?>(worldId == _world.Id && revisionId == _environment.Id ? _environment : null);

        public Task<StateRevision?> LoadStateRevisionAsync(WorldId worldId, RevisionId revisionId, CancellationToken cancellationToken = default)
            => Task.FromResult<StateRevision?>(worldId == _world.Id && revisionId == _state.Id ? _state : null);

        public Task<Stream> OpenRevisionAsync(WorldId worldId, RevisionId revisionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Preparation fails before canonical state materialization.");

        public Task SaveWorldAsync(World world, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task StoreEnvironmentRevisionAsync(EnvironmentRevision revision, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task StoreRevisionAsync(StateRevision revision, Stream package, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingSessionCoordinator : IWorldSessionCoordinator
    {
        public int AcquireCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public Task<WorldSession> GetSessionAsync(WorldId worldId, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorldSession(worldId, SessionState.Available, null, DateTimeOffset.UtcNow));

        public Task<WorldSession> AcquireHostAsync(WorldId worldId, UserIdentity user, CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            return Task.FromResult(new WorldSession(worldId, SessionState.Hosting, user, DateTimeOffset.UtcNow));
        }

        public Task RequestHandoffAsync(WorldId worldId, UserIdentity requestedHost, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CompleteHandoffAsync(WorldId worldId, UserIdentity newHost, RevisionId committedRevision, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReleaseHostAsync(WorldId worldId, UserIdentity user, CancellationToken cancellationToken = default)
        {
            ReleaseCount++;
            return Task.CompletedTask;
        }
    }

    internal sealed class RecordingRecoveryStore : IWorkspaceRecoveryStore
    {
        public Dictionary<WorkspaceId, WorkspaceRecoveryRecord> Records { get; } = [];

        public Task SaveAsync(WorkspaceRecoveryRecord record, CancellationToken cancellationToken = default)
        {
            Records[record.Id] = record;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(WorkspaceId workspaceId, CancellationToken cancellationToken = default)
        {
            Records.Remove(workspaceId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkspaceRecoveryRecord>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRecoveryRecord>>(Records.Values.ToArray());
    }
}
