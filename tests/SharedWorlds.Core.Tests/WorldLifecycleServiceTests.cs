using System.Text;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Errors;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorldLifecycleServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-core-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Import_DoesNotCreateCanonicalWorld_WhenStateStorageFails()
    {
        var storage = new InMemoryWorldStorage { FailStoreRevision = true };
        var sessions = new RecordingSessionCoordinator();
        var recovery = new RecordingWorkspaceRecoveryStore();
        var adapter = new FakeGameAdapter(_root);
        var lifecycle = new WorldLifecycleService(storage, sessions, recovery);

        await Assert.ThrowsAsync<IOException>(() => lifecycle.ImportAsync(
            adapter,
            adapter.Installation,
            adapter.DetectedWorld,
            "Imported World",
            TestUser()));

        Assert.Empty(storage.Worlds);
        Assert.NotNull(adapter.LastCapturedPackagePath);
        Assert.False(File.Exists(adapter.LastCapturedPackagePath));
    }

    [Fact]
    public async Task Import_DefaultsToLocalOnly()
    {
        var storage = new InMemoryWorldStorage();
        var sessions = new RecordingSessionCoordinator();
        var recovery = new RecordingWorkspaceRecoveryStore();
        var adapter = new FakeGameAdapter(_root);
        var lifecycle = new WorldLifecycleService(storage, sessions, recovery);

        var world = await lifecycle.ImportAsync(
            adapter,
            adapter.Installation,
            adapter.DetectedWorld,
            "Private World",
            TestUser());

        Assert.Equal(WorldSharingMode.LocalOnly, world.SharingMode);
        Assert.Equal(WorldSharingMode.LocalOnly, storage.Worlds[world.Id].SharingMode);
    }

    [Fact]
    public async Task Prepare_RejectsWorldOwnedByDifferentAdapter_BeforePreparation()
    {
        var storage = new InMemoryWorldStorage();
        var sessions = new RecordingSessionCoordinator();
        var recovery = new RecordingWorkspaceRecoveryStore();
        var adapter = new FakeGameAdapter(_root);
        var world = new World(
            WorldId.New(),
            "Wrong Adapter",
            "different-adapter",
            [TestUser()],
            RevisionId.New(),
            RevisionId.New());
        storage.Worlds[world.Id] = world;

        var lifecycle = new WorldLifecycleService(storage, sessions, recovery);

        await Assert.ThrowsAsync<AdapterMismatchException>(() => lifecycle.PrepareAsync(
            world.Id,
            adapter,
            adapter.Installation));

        Assert.False(adapter.PrepareCalled);
    }

    [Fact]
    public async Task Prepare_ReportsMissingCanonicalRevisionAsWorldIntegrityFailure()
    {
        var storage = new InMemoryWorldStorage();
        var sessions = new RecordingSessionCoordinator();
        var recovery = new RecordingWorkspaceRecoveryStore();
        var adapter = new FakeGameAdapter(_root);
        var world = new World(
            WorldId.New(),
            "Incomplete World",
            adapter.Id,
            [TestUser()],
            CurrentEnvironmentRevisionId: null,
            CurrentStateRevisionId: RevisionId.New());
        storage.Worlds[world.Id] = world;

        var lifecycle = new WorldLifecycleService(storage, sessions, recovery);

        var exception = await Assert.ThrowsAsync<WorldIntegrityException>(() => lifecycle.PrepareAsync(
            world.Id,
            adapter,
            adapter.Installation));

        Assert.Equal(world.Id, exception.WorldId);
    }

    [Fact]
    public async Task ContinueLocal_AllowsLocalOnlyWorld_AndUsesLocalLaunch()
    {
        var storage = new InMemoryWorldStorage();
        var sessions = new RecordingSessionCoordinator();
        var recovery = new RecordingWorkspaceRecoveryStore();
        var adapter = new FakeGameAdapter(_root);
        var user = TestUser();
        var world = SeedPlayableWorld(storage, adapter, user, WorldSharingMode.LocalOnly);
        var lifecycle = new WorldLifecycleService(storage, sessions, recovery);

        var updated = await lifecycle.ContinueLocalAsync(
            world.Id,
            adapter,
            adapter.Installation,
            user);

        Assert.NotEqual(world.CurrentStateRevisionId, updated.CurrentStateRevisionId);
        Assert.Equal(1, adapter.LocalLaunchCount);
        Assert.Equal(0, adapter.HostLaunchCount);
        Assert.Equal(1, sessions.AcquireCount);
        Assert.Equal(1, sessions.ReleaseCount);
    }

    [Fact]
    public async Task ContinueAsHost_AllowsLocalOnlyWorld_WithoutChangingSharingMode()
    {
        var storage = new InMemoryWorldStorage();
        var sessions = new RecordingSessionCoordinator();
        var recovery = new RecordingWorkspaceRecoveryStore();
        var adapter = new FakeGameAdapter(_root);
        var user = TestUser();
        var world = SeedPlayableWorld(storage, adapter, user, WorldSharingMode.LocalOnly);
        var lifecycle = new WorldLifecycleService(storage, sessions, recovery);

        var updated = await lifecycle.ContinueAsHostAsync(
            world.Id,
            adapter,
            adapter.Installation,
            user);

        Assert.NotEqual(world.CurrentStateRevisionId, updated.CurrentStateRevisionId);
        Assert.Equal(WorldSharingMode.LocalOnly, updated.SharingMode);
        Assert.Equal(WorldSharingMode.LocalOnly, storage.Worlds[world.Id].SharingMode);
        Assert.Equal(0, adapter.LocalLaunchCount);
        Assert.Equal(1, adapter.HostLaunchCount);
        Assert.Equal(1, sessions.AcquireCount);
        Assert.Equal(1, sessions.ReleaseCount);
    }

    [Fact]
    public async Task Continue_PreservesWorkspaceForRecovery_WhenPostLaunchCommitFails()
    {
        var storage = new InMemoryWorldStorage();
        var sessions = new RecordingSessionCoordinator();
        var recovery = new RecordingWorkspaceRecoveryStore();
        var adapter = new FakeGameAdapter(_root);
        var user = TestUser();
        var world = SeedPlayableWorld(storage, adapter, user, WorldSharingMode.Shared);
        var originalHead = world.CurrentStateRevisionId;
        storage.FailStoreRevision = true;

        var lifecycle = new WorldLifecycleService(storage, sessions, recovery);

        await Assert.ThrowsAsync<IOException>(() => lifecycle.ContinueAsHostAsync(
            world.Id,
            adapter,
            adapter.Installation,
            user));

        Assert.Equal(1, sessions.AcquireCount);
        Assert.Equal(1, sessions.ReleaseCount);
        Assert.Equal(originalHead, storage.Worlds[world.Id].CurrentStateRevisionId);
        Assert.NotNull(adapter.LastCapturedPackagePath);
        Assert.False(File.Exists(adapter.LastCapturedPackagePath));
        Assert.Equal(PreparedWorldDisposition.PreserveForRecovery, adapter.LastFinalizationDisposition);
        Assert.NotNull(adapter.LastPreparedWorkspacePath);
        Assert.True(Directory.Exists(adapter.LastPreparedWorkspacePath));

        var record = Assert.Single(recovery.Records.Values);
        Assert.Equal(WorkspaceRecoveryStatus.RecoveryPending, record.Status);
        Assert.Equal(world.Id, record.WorldId);
    }

    [Fact]
    public async Task Continue_DiscardsWorkspaceAndRecoveryRecord_AfterSuccessfulCommit()
    {
        var storage = new InMemoryWorldStorage();
        var sessions = new RecordingSessionCoordinator();
        var recovery = new RecordingWorkspaceRecoveryStore();
        var adapter = new FakeGameAdapter(_root);
        var user = TestUser();
        var world = SeedPlayableWorld(storage, adapter, user, WorldSharingMode.Shared);

        var lifecycle = new WorldLifecycleService(storage, sessions, recovery);

        var updated = await lifecycle.ContinueAsHostAsync(
            world.Id,
            adapter,
            adapter.Installation,
            user);

        Assert.NotEqual(world.CurrentStateRevisionId, updated.CurrentStateRevisionId);
        Assert.Equal(1, adapter.HostLaunchCount);
        Assert.Equal(PreparedWorldDisposition.Discard, adapter.LastFinalizationDisposition);
        Assert.NotNull(adapter.LastPreparedWorkspacePath);
        Assert.False(Directory.Exists(adapter.LastPreparedWorkspacePath));
        Assert.Empty(recovery.Records);
    }

    private static World SeedPlayableWorld(
        InMemoryWorldStorage storage,
        FakeGameAdapter adapter,
        UserIdentity user,
        WorldSharingMode sharingMode)
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
            "Playable World",
            adapter.Id,
            [user],
            environmentId,
            stateId)
        {
            SharingMode = sharingMode
        };

        storage.Worlds[worldId] = world;
        storage.EnvironmentRevisions[(worldId, environmentId)] = environment;
        storage.StateRevisions[(worldId, stateId)] = state;
        storage.StatePayloads[(worldId, stateId)] = Encoding.UTF8.GetBytes("seed-state");
        return world;
    }

    private static UserIdentity TestUser()
        => new("local", "tester", "Tester");

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
            // Test cleanup must not hide the actual assertion result.
        }
        catch (UnauthorizedAccessException)
        {
            // Test cleanup must not hide the actual assertion result.
        }
    }

    private sealed class FakeGameAdapter : IGameAdapter
    {
        private readonly string _root;

        public FakeGameAdapter(string root)
        {
            _root = root;
            Directory.CreateDirectory(root);
            Installation = new GameInstallation(
                "fake-installation",
                root,
                "test");
            DetectedWorld = new DetectedWorld(
                "fake-world",
                "Fake World",
                Path.Combine(root, "source.world"));
            Manifest = new EnvironmentManifest(
                1,
                Id,
                "1.0.0",
                [],
                new Dictionary<string, string>());
        }

        public string Id => "fake";
        public string DisplayName => "Fake Game";
        public GameAdapterCapabilities Capabilities =>
            GameAdapterCapabilities.AutomaticLocalLaunch |
            GameAdapterCapabilities.AutomaticHostLaunch;
        public GameInstallation Installation { get; }
        public DetectedWorld DetectedWorld { get; }
        public EnvironmentManifest Manifest { get; }
        public bool PrepareCalled { get; private set; }
        public int LocalLaunchCount { get; private set; }
        public int HostLaunchCount { get; private set; }
        public string? LastCapturedPackagePath { get; private set; }
        public string? LastPreparedWorkspacePath { get; private set; }
        public PreparedWorldDisposition? LastFinalizationDisposition { get; private set; }

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameInstallation>>([Installation]);

        public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
            GameInstallation installation,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DetectedWorld>>([DetectedWorld]);

        public Task<EnvironmentManifest> InspectEnvironmentAsync(
            GameInstallation installation,
            DetectedWorld world,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Manifest);

        public Task<EnvironmentVerificationReport> VerifyEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            CancellationToken cancellationToken = default)
            => Task.FromResult(EnvironmentVerificationReport.Ready());

        public Task<CapturedState> CaptureDetectedWorldAsync(
            GameInstallation installation,
            DetectedWorld world,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CreateTemporaryCapture("import"));

        public Task<PreparedWorld> PrepareEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            CancellationToken cancellationToken = default)
        {
            PrepareCalled = true;
            var preparedPath = Path.Combine(_root, $"prepared-{Guid.NewGuid():N}");
            Directory.CreateDirectory(preparedPath);
            LastPreparedWorkspacePath = preparedPath;
            return Task.FromResult(new PreparedWorld(
                installation,
                preparedPath,
                requiredEnvironment));
        }

        public Task<CapturedState> CaptureStateAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CreateTemporaryCapture("continue"));

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
            return Task.FromResult(new GameSessionHandle(12345, DateTimeOffset.UtcNow));
        }

        public Task<GameSessionHandle> LaunchHostAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
        {
            HostLaunchCount++;
            return Task.FromResult(new GameSessionHandle(12345, DateTimeOffset.UtcNow));
        }

        public Task<GameSessionHandle> LaunchClientAsync(
            PreparedWorld world,
            HostConnection host,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GameSessionHandle(12345, DateTimeOffset.UtcNow));

        public Task WaitForSessionEndAsync(
            GameSessionHandle session,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task FinalizePreparedWorldAsync(
            PreparedWorld world,
            PreparedWorldDisposition disposition,
            CancellationToken cancellationToken = default)
        {
            LastFinalizationDisposition = disposition;
            if (disposition == PreparedWorldDisposition.Discard &&
                Directory.Exists(world.WorkingDirectory))
            {
                Directory.Delete(world.WorkingDirectory, recursive: true);
            }

            return Task.CompletedTask;
        }

        private CapturedState CreateTemporaryCapture(string prefix)
        {
            var path = Path.Combine(_root, $"{prefix}-{Guid.NewGuid():N}.package");
            File.WriteAllText(path, "captured-state");
            LastCapturedPackagePath = path;
            return new CapturedState(
                new StatePackage(Path.GetFileNameWithoutExtension(path), path),
                DateTimeOffset.UtcNow,
                DeletePackageAfterStore: true);
        }
    }

    private sealed class RecordingSessionCoordinator : IWorldSessionCoordinator
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

    private sealed class RecordingWorkspaceRecoveryStore : IWorkspaceRecoveryStore
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

    private sealed class InMemoryWorldStorage : IWorldStorage
    {
        public Dictionary<WorldId, World> Worlds { get; } = [];
        public Dictionary<(WorldId, RevisionId), EnvironmentRevision> EnvironmentRevisions { get; } = [];
        public Dictionary<(WorldId, RevisionId), StateRevision> StateRevisions { get; } = [];
        public Dictionary<(WorldId, RevisionId), byte[]> StatePayloads { get; } = [];
        public bool FailStoreRevision { get; set; }

        public Task SaveWorldAsync(
            World world,
            CancellationToken cancellationToken = default)
        {
            Worlds[world.Id] = world;
            return Task.CompletedTask;
        }

        public Task<World?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            Worlds.TryGetValue(worldId, out var world);
            return Task.FromResult(world);
        }

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
        {
            EnvironmentRevisions.TryGetValue((worldId, revisionId), out var revision);
            return Task.FromResult(revision);
        }

        public async Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
        {
            if (FailStoreRevision)
            {
                throw new IOException("Injected storage failure.");
            }

            using var memory = new MemoryStream();
            await package.CopyToAsync(memory, cancellationToken);
            StateRevisions[(revision.WorldId, revision.Id)] = revision;
            StatePayloads[(revision.WorldId, revision.Id)] = memory.ToArray();
        }

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            StateRevisions.TryGetValue((worldId, revisionId), out var revision);
            return Task.FromResult(revision);
        }

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            if (!StatePayloads.TryGetValue((worldId, revisionId), out var payload))
            {
                throw new FileNotFoundException("Test state payload does not exist.");
            }

            Stream stream = new MemoryStream(payload, writable: false);
            return Task.FromResult(stream);
        }
    }
}
