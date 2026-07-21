using System.Text;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Core.Tests.Worlds;

public sealed class WorldLifecycleFailureContractTests
{
    [Fact]
    public async Task LaunchFailureDiscardsPreparedWorkspaceWithoutRecoveryNeeded()
    {
        using var fixture = new Fixture { FailLaunch = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunAsync());

        Assert.DoesNotContain(
            fixture.Observer.Changes,
            change => change.Phase == WorldLifecyclePhase.RecoveryNeeded);
        Assert.Empty(await fixture.RecoveryStore.ListAsync());
        Assert.Equal(PreparedWorldDisposition.Discard, fixture.Adapter.FinalDisposition);
        Assert.Null(fixture.Gate.ActiveWorldId);
        Assert.Equal(1, fixture.Coordinator.ReleaseCount);
    }

    [Fact]
    public async Task FailureAfterSessionStartPreservesWorkspaceAndRecoveryRecord()
    {
        using var fixture = new Fixture { FailWaitForEnd = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunAsync());

        var recovery = Assert.Single(await fixture.RecoveryStore.ListAsync());
        Assert.Equal(WorkspaceRecoveryStatus.RecoveryPending, recovery.Status);
        Assert.Equal(PreparedWorldDisposition.PreserveForRecovery, fixture.Adapter.FinalDisposition);
        Assert.Contains(
            fixture.Observer.Changes,
            change => change.Phase == WorldLifecyclePhase.RecoveryNeeded);
        Assert.Null(fixture.Gate.ActiveWorldId);
        Assert.Equal(1, fixture.Coordinator.ReleaseCount);
    }

    [Fact]
    public async Task CaptureFailureAfterSessionEndRemainsRecoveryNeeded()
    {
        using var fixture = new Fixture { FailCapture = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunAsync());

        var recovery = Assert.Single(await fixture.RecoveryStore.ListAsync());
        Assert.Equal(WorkspaceRecoveryStatus.RecoveryPending, recovery.Status);
        Assert.Equal(PreparedWorldDisposition.PreserveForRecovery, fixture.Adapter.FinalDisposition);
        Assert.Contains(
            fixture.Observer.Changes,
            change => change.Phase == WorldLifecyclePhase.RecoveryNeeded);
        Assert.Equal(fixture.InitialStateRevision.Id, fixture.Storage.World.CurrentStateRevisionId);
    }

    [Fact]
    public async Task CleanupFailureAfterCommitEndsAtCleanupPendingNotCompleted()
    {
        using var fixture = new Fixture { FailFinalize = true };

        var updated = await fixture.RunAsync();

        var recovery = Assert.Single(await fixture.RecoveryStore.ListAsync());
        Assert.Equal(WorkspaceRecoveryStatus.CleanupPending, recovery.Status);
        Assert.NotEqual(fixture.InitialStateRevision.Id, updated.CurrentStateRevisionId);
        Assert.Equal(updated.CurrentStateRevisionId, fixture.Storage.World.CurrentStateRevisionId);
        Assert.Contains(
            fixture.Observer.Changes,
            change => change.Phase == WorldLifecyclePhase.CleanupPending);
        Assert.DoesNotContain(
            fixture.Observer.Changes,
            change => change.Phase == WorldLifecyclePhase.Completed);
        Assert.Null(fixture.Gate.ActiveWorldId);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _rootPath;

        public Fixture()
        {
            _rootPath = Path.Combine(
                Path.GetTempPath(),
                "SharedWorlds-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootPath);

            User = new UserIdentity("steam", "steam-a");
            var worldId = WorldId.New();
            var environmentId = RevisionId.New();
            var stateId = RevisionId.New();
            var manifest = new EnvironmentManifest(
                1,
                "test-adapter",
                "1.0",
                Array.Empty<EnvironmentComponent>(),
                new Dictionary<string, string>());

            InitialEnvironmentRevision = new EnvironmentRevision(
                environmentId,
                worldId,
                ParentRevisionId: null,
                CreatedAt: DateTimeOffset.UtcNow,
                CreatedBy: User,
                Manifest: manifest);
            InitialStateRevision = new StateRevision(
                stateId,
                worldId,
                ParentRevisionId: null,
                CreatedAt: DateTimeOffset.UtcNow,
                CreatedBy: User,
                AdapterId: "test-adapter",
                StatePackageId: "state-0");
            var world = new World(
                worldId,
                "Failure Test World",
                "test-adapter",
                new[] { User },
                environmentId,
                stateId);

            Storage = new FakeStorage(
                world,
                InitialEnvironmentRevision,
                InitialStateRevision,
                Encoding.UTF8.GetBytes("initial-state"));
            Coordinator = new FakeCoordinator();
            RecoveryStore = new FakeRecoveryStore();
            Gate = new ManagedWritableSessionGate();
            Observer = new RecordingObserver();
            Adapter = new ConfigurableAdapter(_rootPath, manifest, this);
            Installation = new GameInstallation("install", _rootPath, "test");
            Service = new WorldLifecycleService(
                Storage,
                Coordinator,
                RecoveryStore,
                Gate,
                Observer);
        }

        public bool FailLaunch { get; init; }
        public bool FailWaitForEnd { get; init; }
        public bool FailCapture { get; init; }
        public bool FailFinalize { get; init; }

        public UserIdentity User { get; }
        public EnvironmentRevision InitialEnvironmentRevision { get; }
        public StateRevision InitialStateRevision { get; }
        public FakeStorage Storage { get; }
        public FakeCoordinator Coordinator { get; }
        public FakeRecoveryStore RecoveryStore { get; }
        public ManagedWritableSessionGate Gate { get; }
        public RecordingObserver Observer { get; }
        public ConfigurableAdapter Adapter { get; }
        public GameInstallation Installation { get; }
        public WorldLifecycleService Service { get; }

        public Task<World> RunAsync()
            => Service.ContinueLocalAsync(
                Storage.World.Id,
                Adapter,
                Installation,
                User);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_rootPath))
                {
                    Directory.Delete(_rootPath, recursive: true);
                }
            }
            catch (IOException)
            {
                // Test cleanup is best-effort.
            }
            catch (UnauthorizedAccessException)
            {
                // Test cleanup is best-effort.
            }
        }
    }

    private sealed class RecordingObserver : IWorldLifecycleObserver
    {
        public List<WorldLifecyclePhaseChange> Changes { get; } = new();

        public void OnPhaseChanged(WorldLifecyclePhaseChange change)
            => Changes.Add(change);
    }

    private sealed class FakeStorage : IWorldStorage
    {
        private readonly Dictionary<RevisionId, StateRevision> _stateRevisions = new();
        private readonly Dictionary<RevisionId, byte[]> _packages = new();
        private readonly EnvironmentRevision _environmentRevision;

        public FakeStorage(
            World world,
            EnvironmentRevision environmentRevision,
            StateRevision stateRevision,
            byte[] initialBytes)
        {
            World = world;
            _environmentRevision = environmentRevision;
            _stateRevisions.Add(stateRevision.Id, stateRevision);
            _packages.Add(stateRevision.Id, initialBytes);
        }

        public World World { get; private set; }

        public Task SaveWorldAsync(World world, CancellationToken cancellationToken = default)
        {
            World = world;
            return Task.CompletedTask;
        }

        public Task<World?> LoadWorldAsync(WorldId worldId, CancellationToken cancellationToken = default)
            => Task.FromResult<World?>(World.Id == worldId ? World : null);

        public Task StoreEnvironmentRevisionAsync(
            EnvironmentRevision revision,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<EnvironmentRevision?>(
                _environmentRevision.WorldId == worldId && _environmentRevision.Id == revisionId
                    ? _environmentRevision
                    : null);

        public async Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
        {
            using var destination = new MemoryStream();
            await package.CopyToAsync(destination, cancellationToken);
            _stateRevisions[revision.Id] = revision;
            _packages[revision.Id] = destination.ToArray();
        }

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<StateRevision?>(
                _stateRevisions.TryGetValue(revisionId, out var revision) && revision.WorldId == worldId
                    ? revision
                    : null);

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            if (!_stateRevisions.TryGetValue(revisionId, out var revision) ||
                revision.WorldId != worldId ||
                !_packages.TryGetValue(revisionId, out var bytes))
            {
                throw new InvalidOperationException("Revision does not exist.");
            }

            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }

    private sealed class FakeCoordinator : IWorldSessionCoordinator
    {
        public int ReleaseCount { get; private set; }

        public Task<WorldSession> GetSessionAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WorldSession(
                worldId,
                SessionState.Available,
                ActiveHost: null,
                DateTimeOffset.UtcNow));

        public Task<WorldSession> AcquireHostAsync(
            WorldId worldId,
            UserIdentity user,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WorldSession(
                worldId,
                SessionState.Hosting,
                user,
                DateTimeOffset.UtcNow));

        public Task RequestHandoffAsync(
            WorldId worldId,
            UserIdentity requestedHost,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CompleteHandoffAsync(
            WorldId worldId,
            UserIdentity newHost,
            RevisionId committedRevision,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReleaseHostAsync(
            WorldId worldId,
            UserIdentity user,
            CancellationToken cancellationToken = default)
        {
            ReleaseCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRecoveryStore : IWorkspaceRecoveryStore
    {
        private readonly Dictionary<WorkspaceId, WorkspaceRecoveryRecord> _records = new();

        public Task SaveAsync(
            WorkspaceRecoveryRecord record,
            CancellationToken cancellationToken = default)
        {
            _records[record.Id] = record;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            WorkspaceId workspaceId,
            CancellationToken cancellationToken = default)
        {
            _records.Remove(workspaceId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkspaceRecoveryRecord>> ListAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRecoveryRecord>>(_records.Values.ToArray());
    }

    private sealed class ConfigurableAdapter : IGameAdapter
    {
        private readonly string _rootPath;
        private readonly EnvironmentManifest _manifest;
        private readonly Fixture _fixture;

        public ConfigurableAdapter(
            string rootPath,
            EnvironmentManifest manifest,
            Fixture fixture)
        {
            _rootPath = rootPath;
            _manifest = manifest;
            _fixture = fixture;
        }

        public string Id => "test-adapter";
        public string DisplayName => "Test Adapter";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.AutomaticLocalLaunch;
        public PreparedWorldDisposition? FinalDisposition { get; private set; }

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameInstallation>>(Array.Empty<GameInstallation>());

        public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
            GameInstallation installation,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DetectedWorld>>(Array.Empty<DetectedWorld>());

        public Task<EnvironmentManifest> InspectEnvironmentAsync(
            GameInstallation installation,
            DetectedWorld world,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_manifest);

        public Task<CapturedState> CaptureDetectedWorldAsync(
            GameInstallation installation,
            DetectedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PreparedWorld> PrepareEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            CancellationToken cancellationToken = default)
        {
            var workingDirectory = Path.Combine(_rootPath, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDirectory);
            return Task.FromResult(new PreparedWorld(installation, workingDirectory, requiredEnvironment));
        }

        public Task<CapturedState> CaptureStateAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
        {
            if (_fixture.FailCapture)
            {
                throw new InvalidOperationException("Injected capture failure.");
            }

            var packagePath = Path.Combine(_rootPath, $"capture-{Guid.NewGuid():N}.package");
            File.WriteAllText(packagePath, "updated-state");
            return Task.FromResult(new CapturedState(
                new StatePackage("captured-state", packagePath),
                DateTimeOffset.UtcNow,
                DeletePackageAfterStore: true));
        }

        public Task RestoreStateAsync(
            PreparedWorld world,
            StatePackage state,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<GameSessionHandle> LaunchLocalAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
        {
            if (_fixture.FailLaunch)
            {
                throw new InvalidOperationException("Injected launch failure.");
            }

            return Task.FromResult(new GameSessionHandle(100, DateTimeOffset.UtcNow));
        }

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
        {
            if (_fixture.FailWaitForEnd)
            {
                throw new InvalidOperationException("Injected session-observation failure.");
            }

            return Task.CompletedTask;
        }

        public Task FinalizePreparedWorldAsync(
            PreparedWorld world,
            PreparedWorldDisposition disposition,
            CancellationToken cancellationToken = default)
        {
            FinalDisposition = disposition;
            if (_fixture.FailFinalize)
            {
                throw new InvalidOperationException("Injected workspace-finalization failure.");
            }

            if (disposition == PreparedWorldDisposition.Discard &&
                Directory.Exists(world.WorkingDirectory))
            {
                Directory.Delete(world.WorkingDirectory, recursive: true);
            }

            return Task.CompletedTask;
        }
    }
}
