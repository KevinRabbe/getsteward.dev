using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Core.Tests.Worlds;

public sealed class WorldLifecycleRecoveryStartGuardTests
{
    [Theory]
    [InlineData(WorkspaceRecoveryStatus.Active, false, WorldLifecyclePhase.RecoveryNeeded)]
    [InlineData(WorkspaceRecoveryStatus.Active, true, WorldLifecyclePhase.RecoveryNeeded)]
    [InlineData(WorkspaceRecoveryStatus.RecoveryPending, false, WorldLifecyclePhase.RecoveryNeeded)]
    [InlineData(WorkspaceRecoveryStatus.RecoveryPending, true, WorldLifecyclePhase.RecoveryNeeded)]
    [InlineData(WorkspaceRecoveryStatus.CleanupPending, false, WorldLifecyclePhase.CleanupPending)]
    [InlineData(WorkspaceRecoveryStatus.CleanupPending, true, WorldLifecyclePhase.CleanupPending)]
    public async Task DurableUnresolvedRecordBlocksWritableLifecycleBeforeCoordinatorAcquire(
        WorkspaceRecoveryStatus status,
        bool host,
        WorldLifecyclePhase expectedPhase)
    {
        var blockingWorldId = WorldId.New();
        var requestedWorldId = WorldId.New();
        var record = CreateRecoveryRecord(blockingWorldId, status);
        var recoveryStore = new MutableRecoveryStore(record);
        var coordinator = new CountingCoordinator();
        var observer = new RecordingObserver();
        var service = new WorldLifecycleService(
            new NeverReachedStorage(),
            coordinator,
            recoveryStore,
            new ManagedWritableSessionGate(),
            observer);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host
                ? service.ContinueAsHostAsync(
                    requestedWorldId,
                    new NeverReachedAdapter(),
                    CreateInstallation(),
                    CreateUser())
                : service.ContinueLocalAsync(
                    requestedWorldId,
                    new NeverReachedAdapter(),
                    CreateInstallation(),
                    CreateUser()));

        Assert.Equal(0, coordinator.AcquireCount);
        Assert.Equal(0, coordinator.ReleaseCount);
        Assert.Single(await recoveryStore.ListAsync());
        Assert.Contains(record.Id.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains(blockingWorldId.ToString(), exception.Message, StringComparison.Ordinal);

        var change = Assert.Single(observer.Changes);
        Assert.Equal(blockingWorldId, change.WorldId);
        Assert.Null(change.Mode);
        Assert.Equal(expectedPhase, change.Phase);
    }

    [Fact]
    public async Task RemovingDurableResponsibilityAllowsNextWritableLifecycleToReachCoordinator()
    {
        var record = CreateRecoveryRecord(WorldId.New(), WorkspaceRecoveryStatus.RecoveryPending);
        var recoveryStore = new MutableRecoveryStore(record);
        await recoveryStore.RemoveAsync(record.Id);

        var coordinator = new CountingCoordinator();
        var service = new WorldLifecycleService(
            new NeverReachedStorage(),
            coordinator,
            recoveryStore,
            new ManagedWritableSessionGate());

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.ContinueLocalAsync(
                WorldId.New(),
                new NeverReachedAdapter(),
                CreateInstallation(),
                CreateUser()));

        Assert.Equal(1, coordinator.AcquireCount);
        Assert.Equal(1, coordinator.ReleaseCount);
    }

    [Fact]
    public async Task RecoveryStoreReadFailureFailsClosedBeforeCoordinatorAcquire()
    {
        var recoveryStore = new MutableRecoveryStore
        {
            ThrowOnList = true
        };
        var coordinator = new CountingCoordinator();
        var service = new WorldLifecycleService(
            new NeverReachedStorage(),
            coordinator,
            recoveryStore,
            new ManagedWritableSessionGate());

        await Assert.ThrowsAsync<IOException>(() =>
            service.ContinueAsHostAsync(
                WorldId.New(),
                new NeverReachedAdapter(),
                CreateInstallation(),
                CreateUser()));

        Assert.Equal(0, coordinator.AcquireCount);
        Assert.Equal(0, coordinator.ReleaseCount);
    }

    private static WorkspaceRecoveryRecord CreateRecoveryRecord(
        WorldId worldId,
        WorkspaceRecoveryStatus status)
    {
        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        return new WorkspaceRecoveryRecord(
            WorkspaceId.New(),
            worldId,
            RevisionId.New(),
            "test-adapter",
            "C:/Recovery",
            CreateUser(),
            now,
            now,
            status,
            "test recovery evidence");
    }

    private static UserIdentity CreateUser()
        => new("steam", "steam-a", "Player A");

    private static GameInstallation CreateInstallation()
        => new("install", "C:/Game", "test");

    private sealed class MutableRecoveryStore : IWorkspaceRecoveryStore
    {
        private readonly Dictionary<WorkspaceId, WorkspaceRecoveryRecord> _records = new();

        public MutableRecoveryStore(params WorkspaceRecoveryRecord[] records)
        {
            foreach (var record in records)
            {
                _records.Add(record.Id, record);
            }
        }

        public bool ThrowOnList { get; init; }

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
        {
            if (ThrowOnList)
            {
                throw new IOException("Injected recovery-store read failure.");
            }

            return Task.FromResult<IReadOnlyList<WorkspaceRecoveryRecord>>(_records.Values.ToArray());
        }
    }

    private sealed class RecordingObserver : IWorldLifecycleObserver
    {
        public List<WorldLifecyclePhaseChange> Changes { get; } = new();

        public void OnPhaseChanged(WorldLifecyclePhaseChange change)
        {
            ArgumentNullException.ThrowIfNull(change);
            Changes.Add(change);
        }
    }

    private sealed class CountingCoordinator : IWorldSessionCoordinator
    {
        public int AcquireCount { get; private set; }
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

    private sealed class NeverReachedStorage : IWorldStorage
    {
        public Task SaveWorldAsync(World world, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<World?> LoadWorldAsync(WorldId worldId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task StoreEnvironmentRevisionAsync(
            EnvironmentRevision revision,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
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

    private sealed class NeverReachedAdapter : IGameAdapter
    {
        public string Id => "test-adapter";
        public string DisplayName => "Test Adapter";
        public GameAdapterCapabilities Capabilities =>
            GameAdapterCapabilities.AutomaticLocalLaunch |
            GameAdapterCapabilities.AutomaticHostLaunch;

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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

        public Task FinalizePreparedWorldAsync(
            PreparedWorld world,
            PreparedWorldDisposition disposition,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
