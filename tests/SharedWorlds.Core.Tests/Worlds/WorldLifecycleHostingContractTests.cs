using System.Text;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Core.Tests.Worlds;

public sealed class WorldLifecycleHostingContractTests
{
    [Fact]
    public async Task LocalOnlyWorldCanHostWithoutBecomingPersistentlyShared()
    {
        using var fixture = new Fixture();

        var updated = await fixture.Service.ContinueAsHostAsync(
            fixture.World.Id,
            fixture.Adapter,
            fixture.Installation,
            fixture.User);

        Assert.Equal(1, fixture.Adapter.HostLaunchCount);
        Assert.Equal(0, fixture.Adapter.LocalLaunchCount);
        Assert.Equal(WorldSharingMode.LocalOnly, updated.SharingMode);
        Assert.NotEqual(fixture.InitialStateRevision.Id, updated.CurrentStateRevisionId);
        Assert.Equal(updated, fixture.Storage.World);
        Assert.Equal(1, fixture.Coordinator.AcquireCount);
        Assert.Equal(1, fixture.Coordinator.ReleaseCount);
        Assert.Empty(await fixture.RecoveryStore.ListAsync());
        Assert.Equal(PreparedWorldDisposition.Discard, fixture.Adapter.FinalDisposition);
    }

    [Fact]
    public async Task LocalStartAndTemporaryHostReuseLifecycleButCallDifferentAdapterLaunches()
    {
        using var localFixture = new Fixture();
        await localFixture.Service.ContinueLocalAsync(
            localFixture.World.Id,
            localFixture.Adapter,
            localFixture.Installation,
            localFixture.User);

        using var hostFixture = new Fixture();
        await hostFixture.Service.ContinueAsHostAsync(
            hostFixture.World.Id,
            hostFixture.Adapter,
            hostFixture.Installation,
            hostFixture.User);

        Assert.Equal(1, localFixture.Adapter.LocalLaunchCount);
        Assert.Equal(0, localFixture.Adapter.HostLaunchCount);
        Assert.Equal(0, hostFixture.Adapter.LocalLaunchCount);
        Assert.Equal(1, hostFixture.Adapter.HostLaunchCount);
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

            User = new UserIdentity("steam", "steam-a", "Player A");
            var worldId = WorldId.New();
            var environmentId = RevisionId.New();
            var stateId = RevisionId.New();
            var manifest = new EnvironmentManifest(
                SchemaVersion: 1,
                AdapterId: "test-adapter",
                GameVersion: "1.0",
                Components: Array.Empty<EnvironmentComponent>(),
                Configuration: new Dictionary<string, string>());

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
            World = new World(
                worldId,
                "Local World",
                "test-adapter",
                new[] { User },
                environmentId,
                stateId)
            {
                SharingMode = WorldSharingMode.LocalOnly
            };

            Storage = new FakeWorldStorage(
                World,
                InitialEnvironmentRevision,
                InitialStateRevision,
                Encoding.UTF8.GetBytes("initial-state"));
            Coordinator = new FakeSessionCoordinator();
            RecoveryStore = new FakeRecoveryStore();
            Adapter = new FakeGameAdapter(_rootPath, manifest);
            Installation = new GameInstallation("test-install", _rootPath, "test");
            Service = new WorldLifecycleService(Storage, Coordinator, RecoveryStore);
        }

        public UserIdentity User { get; }
        public World World { get; }
        public EnvironmentRevision InitialEnvironmentRevision { get; }
        public StateRevision InitialStateRevision { get; }
        public FakeWorldStorage Storage { get; }
        public FakeSessionCoordinator Coordinator { get; }
        public FakeRecoveryStore RecoveryStore { get; }
        public FakeGameAdapter Adapter { get; }
        public GameInstallation Installation { get; }
        public WorldLifecycleService Service { get; }

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

    private sealed class FakeWorldStorage : IWorldStorage
    {
        private readonly Dictionary<RevisionId, StateRevision> _stateRevisions = new();
        private readonly Dictionary<RevisionId, byte[]> _packages = new();
        private EnvironmentRevision _environmentRevision;

        public FakeWorldStorage(
            World world,
            EnvironmentRevision environmentRevision,
            StateRevision stateRevision,
            byte[] packageBytes)
        {
            World = world;
            _environmentRevision = environmentRevision;
            _stateRevisions.Add(stateRevision.Id, stateRevision);
            _packages.Add(stateRevision.Id, packageBytes);
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
        {
            _environmentRevision = revision;
            return Task.CompletedTask;
        }

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

    private sealed class FakeSessionCoordinator : IWorldSessionCoordinator
    {
        private WorldSession? _session;

        public int AcquireCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public Task<WorldSession> GetSessionAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_session ?? new WorldSession(
                worldId,
                SessionState.Available,
                ActiveHost: null,
                DateTimeOffset.UtcNow));

        public Task<WorldSession> AcquireHostAsync(
            WorldId worldId,
            UserIdentity user,
            CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            _session = new WorldSession(
                worldId,
                SessionState.Hosting,
                user,
                DateTimeOffset.UtcNow);
            return Task.FromResult(_session);
        }

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
            _session = new WorldSession(
                worldId,
                SessionState.Available,
                ActiveHost: null,
                DateTimeOffset.UtcNow);
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

    private sealed class FakeGameAdapter : IGameAdapter
    {
        private readonly string _rootPath;
        private readonly EnvironmentManifest _manifest;

        public FakeGameAdapter(string rootPath, EnvironmentManifest manifest)
        {
            _rootPath = rootPath;
            _manifest = manifest;
        }

        public string Id => "test-adapter";
        public string DisplayName => "Test Adapter";
        public GameAdapterCapabilities Capabilities =>
            GameAdapterCapabilities.AutomaticLocalLaunch |
            GameAdapterCapabilities.AutomaticHostLaunch;

        public int LocalLaunchCount { get; private set; }
        public int HostLaunchCount { get; private set; }
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
            return Task.FromResult(new PreparedWorld(
                installation,
                workingDirectory,
                requiredEnvironment));
        }

        public Task<CapturedState> CaptureStateAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
        {
            var packagePath = Path.Combine(_rootPath, $"captured-{Guid.NewGuid():N}.package");
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
            LocalLaunchCount++;
            return Task.FromResult(new GameSessionHandle(100, DateTimeOffset.UtcNow));
        }

        public Task<GameSessionHandle> LaunchHostAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
        {
            HostLaunchCount++;
            return Task.FromResult(new GameSessionHandle(200, DateTimeOffset.UtcNow));
        }

        public Task<GameSessionHandle> LaunchClientAsync(
            PreparedWorld world,
            HostConnection host,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task WaitForSessionEndAsync(
            GameSessionHandle session,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task FinalizePreparedWorldAsync(
            PreparedWorld world,
            PreparedWorldDisposition disposition,
            CancellationToken cancellationToken = default)
        {
            FinalDisposition = disposition;
            if (disposition == PreparedWorldDisposition.Discard &&
                Directory.Exists(world.WorkingDirectory))
            {
                Directory.Delete(world.WorkingDirectory, recursive: true);
            }

            return Task.CompletedTask;
        }
    }
}
