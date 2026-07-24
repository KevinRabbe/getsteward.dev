using System.Text;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorldLifecycleManagedHostStopTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-host-stop-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task StopRequestEndsActiveHostBeforeCaptureAndNormalCommitContinues()
    {
        var storage = new MemoryStorage();
        var sessions = new SessionCoordinator();
        var recovery = new RecoveryStore();
        var adapter = new BlockingHostAdapter(_root);
        var user = new UserIdentity("local", "tester", "Tester");
        var world = SeedWorld(storage, adapter, user);
        var observer = new RunningObserver(world.Id);
        var lifecycle = new WorldLifecycleService(
            storage,
            sessions,
            recovery,
            new ManagedWritableSessionGate(),
            observer);

        var continueTask = lifecycle.ContinueAsHostAsync(
            world.Id,
            adapter,
            adapter.Installation,
            user);

        await observer.Running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, adapter.CaptureCount);

        var stopAccepted = await lifecycle.RequestHostStopAsync(world.Id);
        var updated = await continueTask;

        Assert.True(stopAccepted);
        Assert.Equal(1, adapter.HostStopCount);
        Assert.Equal(0, adapter.CaptureCountAtStop);
        Assert.Equal(1, adapter.CaptureCount);
        Assert.NotEqual(world.CurrentStateRevisionId, updated.CurrentStateRevisionId);
        Assert.Equal(1, sessions.AcquireCount);
        Assert.Equal(1, sessions.ReleaseCount);
        Assert.Empty(recovery.Records);
        Assert.False(await lifecycle.RequestHostStopAsync(world.Id));
    }

    [Fact]
    public async Task StopRequestWithoutActiveHostDoesNothing()
    {
        var lifecycle = new WorldLifecycleService(
            new MemoryStorage(),
            new SessionCoordinator(),
            new RecoveryStore());

        Assert.False(await lifecycle.RequestHostStopAsync(WorldId.New()));
    }

    private static World SeedWorld(
        MemoryStorage storage,
        BlockingHostAdapter adapter,
        UserIdentity user)
    {
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();
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
            "seed-package");
        var world = new World(
            worldId,
            "Managed host stop",
            adapter.Id,
            [user],
            environmentId,
            stateId)
        {
            SharingMode = WorldSharingMode.LocalOnly
        };

        storage.Worlds[worldId] = world;
        storage.EnvironmentRevisions[(worldId, environmentId)] = environment;
        storage.StateRevisions[(worldId, stateId)] = state;
        storage.StatePayloads[(worldId, stateId)] = Encoding.UTF8.GetBytes("seed");
        return world;
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class BlockingHostAdapter : IGameAdapter
    {
        private readonly string _root;
        private readonly TaskCompletionSource _hostEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public BlockingHostAdapter(string root)
        {
            _root = root;
            Directory.CreateDirectory(root);
            Installation = new GameInstallation("fake", root, "test");
            Manifest = new EnvironmentManifest(
                1,
                Id,
                "1.0.0",
                [],
                new Dictionary<string, string>());
        }

        public string Id => "host-stop-test";
        public string DisplayName => "Host Stop Test";
        public GameAdapterCapabilities Capabilities =>
            GameAdapterCapabilities.AutomaticHostLaunch |
            GameAdapterCapabilities.AutomaticHostStop;
        public GameInstallation Installation { get; }
        public EnvironmentManifest Manifest { get; }
        public int HostStopCount { get; private set; }
        public int CaptureCount { get; private set; }
        public int CaptureCountAtStop { get; private set; } = -1;

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameInstallation>>([Installation]);

        public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
            GameInstallation installation,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DetectedWorld>>([]);

        public Task<EnvironmentManifest> InspectEnvironmentAsync(
            GameInstallation installation,
            DetectedWorld world,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Manifest);

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
            var path = Path.Combine(_root, $"prepared-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return Task.FromResult(new PreparedWorld(installation, path, requiredEnvironment));
        }

        public Task<CapturedState> CaptureStateAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
        {
            CaptureCount++;
            var path = Path.Combine(_root, $"capture-{Guid.NewGuid():N}.package");
            File.WriteAllText(path, "updated");
            return Task.FromResult(new CapturedState(
                new StatePackage(Path.GetFileNameWithoutExtension(path), path),
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
            => throw new NotSupportedException();

        public Task<GameSessionHandle> LaunchHostAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GameSessionHandle(4242, DateTimeOffset.UtcNow));

        public Task RequestHostStopAsync(
            GameSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HostStopCount++;
            CaptureCountAtStop = CaptureCount;
            _hostEnded.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<GameSessionHandle> LaunchClientAsync(
            PreparedWorld world,
            HostConnection host,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task WaitForSessionEndAsync(
            GameSessionHandle session,
            CancellationToken cancellationToken = default)
            => _hostEnded.Task.WaitAsync(cancellationToken);

        public Task FinalizePreparedWorldAsync(
            PreparedWorld world,
            PreparedWorldDisposition disposition,
            CancellationToken cancellationToken = default)
        {
            if (disposition == PreparedWorldDisposition.Discard && Directory.Exists(world.WorkingDirectory))
            {
                Directory.Delete(world.WorkingDirectory, recursive: true);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RunningObserver(WorldId expectedWorldId) : IWorldLifecycleObserver
    {
        public TaskCompletionSource Running { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnPhaseChanged(WorldLifecyclePhaseChange change)
        {
            if (change.WorldId == expectedWorldId && change.Phase == WorldLifecyclePhase.Running)
            {
                Running.TrySetResult();
            }
        }
    }

    private sealed class SessionCoordinator : IWorldSessionCoordinator
    {
        public int AcquireCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public Task<WorldSession> GetSessionAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WorldSession(
                worldId,
                SessionState.Available,
                null,
                DateTimeOffset.UtcNow));

        public Task<WorldSession> AcquireHostAsync(
            WorldId worldId,
            UserIdentity user,
            CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            return Task.FromResult(new WorldSession(
                worldId,
                SessionState.Hosting,
                user,
                DateTimeOffset.UtcNow));
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
            return Task.CompletedTask;
        }
    }

    private sealed class RecoveryStore : IWorkspaceRecoveryStore
    {
        public Dictionary<WorkspaceId, WorkspaceRecoveryRecord> Records { get; } = [];

        public Task SaveAsync(
            WorkspaceRecoveryRecord record,
            CancellationToken cancellationToken = default)
        {
            Records[record.Id] = record;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            WorkspaceId workspaceId,
            CancellationToken cancellationToken = default)
        {
            Records.Remove(workspaceId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkspaceRecoveryRecord>> ListAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRecoveryRecord>>(Records.Values.ToArray());
    }

    private sealed class MemoryStorage : IWorldStorage
    {
        public Dictionary<WorldId, World> Worlds { get; } = [];
        public Dictionary<(WorldId, RevisionId), EnvironmentRevision> EnvironmentRevisions { get; } = [];
        public Dictionary<(WorldId, RevisionId), StateRevision> StateRevisions { get; } = [];
        public Dictionary<(WorldId, RevisionId), byte[]> StatePayloads { get; } = [];

        public Task SaveWorldAsync(World world, CancellationToken cancellationToken = default)
        {
            Worlds[world.Id] = world;
            return Task.CompletedTask;
        }

        public Task<World?> LoadWorldAsync(WorldId worldId, CancellationToken cancellationToken = default)
            => Task.FromResult(Worlds.GetValueOrDefault(worldId));

        public Task StoreEnvironmentRevisionAsync(
            EnvironmentRevision revision,
            CancellationToken cancellationToken = default)
        {
            EnvironmentRevisions[(revision.WorldId, revision.Id)] = revision;
            return Task.CompletedTask;
        }

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(EnvironmentRevisions.GetValueOrDefault((worldId, revisionId)));

        public async Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
        {
            using var memory = new MemoryStream();
            await package.CopyToAsync(memory, cancellationToken);
            StateRevisions[(revision.WorldId, revision.Id)] = revision;
            StatePayloads[(revision.WorldId, revision.Id)] = memory.ToArray();
        }

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(StateRevisions.GetValueOrDefault((worldId, revisionId)));

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream(
                StatePayloads[(worldId, revisionId)],
                writable: false));
    }
}
