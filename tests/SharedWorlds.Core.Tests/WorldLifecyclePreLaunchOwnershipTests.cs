using System.Text;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorldLifecyclePreLaunchOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-prelaunch-ownership-{Guid.NewGuid():N}");

    [Fact]
    public async Task RestoreFailureDiscardsPreparedWorkspaceAndRemovesCleanupJournal()
    {
        var fixture = CreateFixture(failCleanup: false);
        var originalHead = fixture.World.CurrentStateRevisionId;

        await Assert.ThrowsAsync<IOException>(() => fixture.Lifecycle.ContinueAsHostAsync(
            fixture.World.Id,
            fixture.Adapter,
            fixture.Adapter.Installation,
            fixture.User));

        Assert.Equal(1, fixture.Sessions.AcquireCount);
        Assert.Equal(1, fixture.Sessions.ReleaseCount);
        Assert.Equal(0, fixture.Adapter.HostLaunchCount);
        Assert.Equal(PreparedWorldDisposition.Discard, fixture.Adapter.LastFinalizationDisposition);
        Assert.NotNull(fixture.Adapter.LastPreparedWorkspacePath);
        Assert.False(Directory.Exists(fixture.Adapter.LastPreparedWorkspacePath));
        Assert.Empty(fixture.Recovery.Records);
        Assert.Equal(originalHead, fixture.Storage.World.CurrentStateRevisionId);
    }

    [Fact]
    public async Task RestoreFailurePreservesCleanupResponsibilityWhenAdapterCleanupFails()
    {
        var fixture = CreateFixture(failCleanup: true);
        var originalHead = fixture.World.CurrentStateRevisionId;

        await Assert.ThrowsAsync<IOException>(() => fixture.Lifecycle.ContinueAsHostAsync(
            fixture.World.Id,
            fixture.Adapter,
            fixture.Adapter.Installation,
            fixture.User));

        Assert.Equal(1, fixture.Sessions.AcquireCount);
        Assert.Equal(1, fixture.Sessions.ReleaseCount);
        Assert.Equal(0, fixture.Adapter.HostLaunchCount);
        Assert.Equal(PreparedWorldDisposition.Discard, fixture.Adapter.LastFinalizationDisposition);
        Assert.NotNull(fixture.Adapter.LastPreparedWorkspacePath);
        Assert.True(Directory.Exists(fixture.Adapter.LastPreparedWorkspacePath));

        var record = Assert.Single(fixture.Recovery.Records.Values);
        Assert.Equal(WorkspaceRecoveryStatus.CleanupPending, record.Status);
        Assert.Equal(fixture.World.Id, record.WorldId);
        Assert.Equal(fixture.World.CurrentEnvironmentRevisionId, record.EnvironmentRevisionId);
        Assert.Equal(fixture.Adapter.LastPreparedWorkspacePath, record.WorkingDirectory);
        Assert.Contains("could not be discarded", record.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalHead, fixture.Storage.World.CurrentStateRevisionId);
    }

    private Fixture CreateFixture(bool failCleanup)
    {
        Directory.CreateDirectory(_root);
        var user = new UserIdentity("local", "tester", "Tester");
        var adapter = new RestoreFailingAdapter(_root, failCleanup);
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();
        var world = new World(
            worldId,
            "Pre-launch failure",
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
            "seed-package");

        var storage = new FixtureStorage(
            world,
            environment,
            state,
            Encoding.UTF8.GetBytes("seed-state"));
        var sessions = new RecordingSessionCoordinator();
        var recovery = new RecordingRecoveryStore();
        var lifecycle = new WorldLifecycleService(storage, sessions, recovery);
        return new Fixture(
            lifecycle,
            storage,
            sessions,
            recovery,
            adapter,
            world,
            user);
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

    private sealed record Fixture(
        WorldLifecycleService Lifecycle,
        FixtureStorage Storage,
        RecordingSessionCoordinator Sessions,
        RecordingRecoveryStore Recovery,
        RestoreFailingAdapter Adapter,
        World World,
        UserIdentity User);

    private sealed class RestoreFailingAdapter : IGameAdapter
    {
        private readonly string _root;
        private readonly bool _failCleanup;

        public RestoreFailingAdapter(string root, bool failCleanup)
        {
            _root = root;
            _failCleanup = failCleanup;
            Installation = new GameInstallation("fixture", root, "test");
            Manifest = new EnvironmentManifest(
                1,
                Id,
                "1.0",
                [],
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        public string Id => "prelaunch-fixture";
        public string DisplayName => "Pre-launch fixture";
        public GameAdapterCapabilities Capabilities =>
            GameAdapterCapabilities.AutomaticHostLaunch |
            GameAdapterCapabilities.ExactGameVersion;
        public GameInstallation Installation { get; }
        public EnvironmentManifest Manifest { get; }
        public int HostLaunchCount { get; private set; }
        public string? LastPreparedWorkspacePath { get; private set; }
        public PreparedWorldDisposition? LastFinalizationDisposition { get; private set; }

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
            LastPreparedWorkspacePath = path;
            return Task.FromResult(new PreparedWorld(
                installation,
                path,
                requiredEnvironment));
        }

        public Task<EnvironmentVerificationReport> VerifyEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            CancellationToken cancellationToken = default)
            => Task.FromResult(EnvironmentVerificationReport.Ready());

        public Task<CapturedState> CaptureStateAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RestoreStateAsync(
            PreparedWorld world,
            StatePackage state,
            CancellationToken cancellationToken = default)
            => throw new IOException("Injected restore failure after workspace creation.");

        public Task<GameSessionHandle> LaunchHostAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
        {
            HostLaunchCount++;
            return Task.FromResult(new GameSessionHandle(1, DateTimeOffset.UtcNow));
        }

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
            if (_failCleanup)
            {
                throw new IOException("Injected prepared-workspace cleanup failure.");
            }

            if (disposition == PreparedWorldDisposition.Discard &&
                Directory.Exists(world.WorkingDirectory))
            {
                Directory.Delete(world.WorkingDirectory, recursive: true);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FixtureStorage : IWorldStorage
    {
        private readonly EnvironmentRevision _environment;
        private readonly StateRevision _state;
        private readonly byte[] _payload;

        public FixtureStorage(
            World world,
            EnvironmentRevision environment,
            StateRevision state,
            byte[] payload)
        {
            World = world;
            _environment = environment;
            _state = state;
            _payload = payload;
        }

        public World World { get; private set; }

        public Task SaveWorldAsync(World world, CancellationToken cancellationToken = default)
        {
            World = world;
            return Task.CompletedTask;
        }

        public Task<World?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<World?>(worldId == World.Id ? World : null);

        public Task StoreEnvironmentRevisionAsync(
            EnvironmentRevision revision,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<EnvironmentRevision?>(
                worldId == World.Id && revisionId == _environment.Id ? _environment : null);

        public Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<StateRevision?>(
                worldId == World.Id && revisionId == _state.Id ? _state : null);

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            if (worldId != World.Id || revisionId != _state.Id)
            {
                throw new FileNotFoundException();
            }

            Stream stream = new MemoryStream(_payload, writable: false);
            return Task.FromResult(stream);
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

    private sealed class RecordingRecoveryStore : IWorkspaceRecoveryStore
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
}
