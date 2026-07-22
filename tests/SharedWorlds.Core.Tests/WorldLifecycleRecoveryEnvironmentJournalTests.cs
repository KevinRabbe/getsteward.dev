using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorldLifecycleRecoveryEnvironmentJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "steward-lifecycle-environment-journal-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CleanupPendingRecordKeepsExactEnvironmentRevisionThatCreatedWorkspace()
    {
        Directory.CreateDirectory(_root);
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
            "Journal Test",
            "test-adapter",
            [user],
            environmentId,
            stateId);
        var storage = new JournalStorage(
            world,
            new EnvironmentRevision(
                environmentId,
                worldId,
                ParentRevisionId: null,
                DateTimeOffset.UtcNow,
                user,
                manifest),
            new StateRevision(
                stateId,
                worldId,
                ParentRevisionId: null,
                DateTimeOffset.UtcNow,
                user,
                "test-adapter",
                "initial"));
        var recovery = new JournalRecoveryStore();
        var adapter = new JournalAdapter(_root);
        var lifecycle = new WorldLifecycleService(
            storage,
            new JournalSessionCoordinator(),
            recovery);

        var updated = await lifecycle.ContinueLocalAsync(
            worldId,
            adapter,
            adapter.Installation,
            user);

        Assert.NotEqual(stateId, updated.CurrentStateRevisionId);
        var record = Assert.Single(recovery.Records);
        Assert.Equal(WorkspaceRecoveryStatus.CleanupPending, record.Status);
        Assert.Equal(environmentId, record.EnvironmentRevisionId);
        Assert.NotNull(record.CandidateStateRevisionId);
        Assert.True(Directory.Exists(record.WorkingDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class JournalStorage : IWorldStorage
    {
        private World _world;
        private readonly EnvironmentRevision _environment;
        private readonly Dictionary<RevisionId, StateRevision> _states = [];
        private readonly Dictionary<RevisionId, byte[]> _payloads = [];

        public JournalStorage(
            World world,
            EnvironmentRevision environment,
            StateRevision initialState)
        {
            _world = world;
            _environment = environment;
            _states[initialState.Id] = initialState;
            _payloads[initialState.Id] = "initial"u8.ToArray();
        }

        public Task SaveWorldAsync(World world, CancellationToken cancellationToken = default)
        {
            _world = world;
            return Task.CompletedTask;
        }

        public Task<World?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<World?>(_world.Id == worldId ? _world : null);

        public Task StoreEnvironmentRevisionAsync(
            EnvironmentRevision revision,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<EnvironmentRevision?>(
                _environment.WorldId == worldId && _environment.Id == revisionId
                    ? _environment
                    : null);

        public async Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
        {
            using var memory = new MemoryStream();
            await package.CopyToAsync(memory, cancellationToken);
            _states[revision.Id] = revision;
            _payloads[revision.Id] = memory.ToArray();
        }

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<StateRevision?>(
                _states.TryGetValue(revisionId, out var revision) && revision.WorldId == worldId
                    ? revision
                    : null);

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(
                new MemoryStream(_payloads[revisionId], writable: false));
    }

    private sealed class JournalRecoveryStore : IWorkspaceRecoveryStore
    {
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

    private sealed class JournalSessionCoordinator : IWorldSessionCoordinator
    {
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
            => Task.FromResult(new WorldSession(
                worldId,
                SessionState.Hosting,
                user,
                DateTimeOffset.UtcNow));

        public Task ReleaseHostAsync(
            WorldId worldId,
            UserIdentity user,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RequestHandoffAsync(
            WorldId worldId,
            UserIdentity requestedHost,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CompleteHandoffAsync(
            WorldId worldId,
            UserIdentity newHost,
            RevisionId committedRevision,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class JournalAdapter : IGameAdapter
    {
        private readonly string _root;

        public JournalAdapter(string root)
        {
            _root = root;
            Installation = new GameInstallation("test-installation", root, "test");
        }

        public string Id => "test-adapter";
        public string DisplayName => "Journal Test Adapter";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.AutomaticLocalLaunch;
        public GameInstallation Installation { get; }

        public Task<PreparedWorld> PrepareEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            CancellationToken cancellationToken = default)
        {
            var workspace = Path.Combine(_root, $"workspace-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workspace);
            return Task.FromResult(new PreparedWorld(
                installation,
                workspace,
                requiredEnvironment));
        }

        public Task RestoreStateAsync(
            PreparedWorld world,
            StatePackage state,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<GameSessionHandle> LaunchLocalAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GameSessionHandle(1, DateTimeOffset.UtcNow));

        public Task WaitForSessionEndAsync(
            GameSessionHandle session,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<CapturedState> CaptureStateAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
        {
            var path = Path.Combine(_root, $"capture-{Guid.NewGuid():N}.package");
            File.WriteAllText(path, "next");
            return Task.FromResult(new CapturedState(
                new StatePackage("next", path),
                DateTimeOffset.UtcNow,
                DeletePackageAfterStore: true));
        }

        public Task FinalizePreparedWorldAsync(
            PreparedWorld world,
            PreparedWorldDisposition disposition,
            CancellationToken cancellationToken = default)
            => throw new IOException("Injected cleanup failure after canonical commit.");

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

        public Task<GameSessionHandle> LaunchHostAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GameSessionHandle> LaunchClientAsync(
            PreparedWorld world,
            HostConnection host,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
