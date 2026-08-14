using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Core.Storage;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorldLifecycleManagedFinalizationIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "safeworld-lifecycle-managed-finalization",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SuccessfulManagedSessionReleasesAdapterThenCoreDeletesExactWorkspaceAndJournal()
    {
        Directory.CreateDirectory(_root);
        var user = new UserIdentity("test", "user", "Test User");
        var adapter = new SuccessfulPlannerAdapter(_root);
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();
        var world = new World(
            worldId,
            "Managed finalization test",
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
        var storage = new SuccessfulStorage(world, environment, state);
        var sessions = new RecordingSessionCoordinator();
        var recovery = new RecordingRecoveryStore();
        var managed = new ManagedWorkspaceStorage(Path.Combine(_root, "managed"));
        var lifecycle = new WorldLifecycleService(
            storage,
            sessions,
            recovery,
            new ManagedWritableSessionGate(),
            NullWorldLifecycleObserver.Instance,
            managed);

        var updated = await lifecycle.ContinueAsHostAsync(
            worldId,
            adapter,
            adapter.Installation,
            user);

        Assert.NotEqual(stateId, updated.CurrentStateRevisionId);
        Assert.Equal(1, sessions.AcquireCount);
        Assert.Equal(1, sessions.ReleaseCount);
        Assert.Equal(
            PreparedWorldDisposition.ReleaseForCoreManagedDiscard,
            adapter.ObservedFinalizationDisposition);
        Assert.True(adapter.WorkspaceExistedDuringFinalization);
        Assert.NotNull(adapter.MaterializedWorkingDirectory);
        Assert.False(Directory.Exists(adapter.MaterializedWorkingDirectory));
        Assert.Empty(recovery.Records);
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

    private sealed class SuccessfulPlannerAdapter : IGameAdapter, IPreparedWorldRecoveryPlanner
    {
        private readonly string _root;

        public SuccessfulPlannerAdapter(string root)
        {
            _root = root;
            Installation = new GameInstallation("test", root, "test");
        }

        public string Id => "managed-finalization-integration";
        public string DisplayName => "Managed finalization integration";
        public GameAdapterCapabilities Capabilities =>
            GameAdapterCapabilities.AutomaticHostLaunch |
            GameAdapterCapabilities.ExactGameVersion;
        public GameInstallation Installation { get; }
        public EnvironmentManifest Manifest => new(
            1,
            Id,
            "1.0",
            [],
            new Dictionary<string, string>());
        public string? MaterializedWorkingDirectory { get; private set; }
        public PreparedWorldDisposition? ObservedFinalizationDisposition { get; private set; }
        public bool WorkspaceExistedDuringFinalization { get; private set; }

        public Task<PreparedWorldRecoveryLocation> PlanPreparedWorldRecoveryAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            PreparedWorldPreparationContext preparation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(Directory.Exists(preparation.ManagedWorkingDirectory));
            return Task.FromResult(PreparedWorldRecoveryLocation.Managed());
        }

        public Task<PreparedWorld> PrepareEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            PreparedWorldPreparationContext preparation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Directory.Exists(preparation.ManagedWorkingDirectory));
            MaterializedWorkingDirectory = Path.GetFullPath(preparation.ManagedWorkingDirectory);
            File.WriteAllText(
                Path.Combine(MaterializedWorkingDirectory, "materialized.txt"),
                "materialized");
            return Task.FromResult(new PreparedWorld(
                installation,
                MaterializedWorkingDirectory,
                requiredEnvironment,
                RecoveryLocation: PreparedWorldRecoveryLocation.Managed()));
        }

        public Task<EnvironmentVerificationReport> VerifyEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            CancellationToken cancellationToken = default)
            => Task.FromResult(EnvironmentVerificationReport.Ready());

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
            => throw new InvalidOperationException(
                "Legacy preparation must not be used for planner-backed writable sessions.");

        public async Task RestoreStateAsync(
            PreparedWorld world,
            StatePackage state,
            CancellationToken cancellationToken = default)
        {
            await using var source = new FileStream(
                state.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                useAsync: true);
            await using var destination = new FileStream(
                Path.Combine(world.WorkingDirectory, "restored.package"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                useAsync: true);
            await source.CopyToAsync(destination, cancellationToken);
        }

        public Task<GameSessionHandle> LaunchHostAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Directory.Exists(world.WorkingDirectory));
            return Task.FromResult(new GameSessionHandle(42, DateTimeOffset.UtcNow));
        }

        public Task WaitForSessionEndAsync(
            GameSessionHandle session,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<CapturedState> CaptureStateAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var package = Path.Combine(_root, $"captured-{Guid.NewGuid():N}.package");
            File.WriteAllText(package, "captured-state");
            return Task.FromResult(new CapturedState(
                new StatePackage("captured", package),
                DateTimeOffset.UtcNow));
        }

        public Task FinalizePreparedWorldAsync(
            PreparedWorld world,
            PreparedWorldDisposition disposition,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObservedFinalizationDisposition = disposition;
            WorkspaceExistedDuringFinalization = Directory.Exists(world.WorkingDirectory);
            Assert.Equal(
                PreparedWorldDisposition.ReleaseForCoreManagedDiscard,
                disposition);
            Assert.True(WorkspaceExistedDuringFinalization);
            return Task.CompletedTask;
        }
    }

    private sealed class SuccessfulStorage : IWorldStorage
    {
        private World _world;
        private readonly EnvironmentRevision _environment;
        private readonly Dictionary<RevisionId, StateRevision> _states = [];
        private readonly Dictionary<RevisionId, byte[]> _payloads = [];

        public SuccessfulStorage(
            World world,
            EnvironmentRevision environment,
            StateRevision state)
        {
            _world = world;
            _environment = environment;
            _states[state.Id] = state;
            _payloads[state.Id] = "seed-state"u8.ToArray();
        }

        public Task<World?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<World?>(worldId == _world.Id ? _world : null);

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<EnvironmentRevision?>(
                worldId == _world.Id && revisionId == _environment.Id
                    ? _environment
                    : null);

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<StateRevision?>(
                worldId == _world.Id && _states.TryGetValue(revisionId, out var state)
                    ? state
                    : null);

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            if (worldId != _world.Id || !_payloads.TryGetValue(revisionId, out var payload))
            {
                throw new InvalidOperationException("Requested test revision is unavailable.");
            }

            return Task.FromResult<Stream>(new MemoryStream(payload, writable: false));
        }

        public Task SaveWorldAsync(
            World world,
            CancellationToken cancellationToken = default)
        {
            _world = world;
            return Task.CompletedTask;
        }

        public Task StoreEnvironmentRevisionAsync(
            EnvironmentRevision revision,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
        {
            await using var memory = new MemoryStream();
            await package.CopyToAsync(memory, cancellationToken);
            _states[revision.Id] = revision;
            _payloads[revision.Id] = memory.ToArray();
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
            => Task.FromResult<IReadOnlyList<WorkspaceRecoveryRecord>>(
                Records.Values.ToArray());
    }
}
