using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Core.Tests.Worlds;

public sealed class WorldLifecycleReservationAcquisitionTests
{
    [Theory]
    [InlineData(false, ManagedWorldSessionMode.Local)]
    [InlineData(true, ManagedWorldSessionMode.Hosted)]
    public async Task ProvenFailedAcquireClearsTransientResponsibilityWithoutRelease(
        bool host,
        ManagedWorldSessionMode expectedMode)
    {
        var worldId = WorldId.New();
        var coordinator = new FailingAcquireCoordinator();
        var observer = new TrackingObserver();
        var service = new WorldLifecycleService(
            new NeverReachedStorage(),
            coordinator,
            new EmptyRecoveryStore(),
            new ManagedWritableSessionGate(),
            observer);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host
                ? service.ContinueAsHostAsync(
                    worldId,
                    new NeverReachedAdapter(),
                    new GameInstallation("install", "C:/Game", "test"),
                    new UserIdentity("steam", "steam-a"))
                : service.ContinueLocalAsync(
                    worldId,
                    new NeverReachedAdapter(),
                    new GameInstallation("install", "C:/Game", "test"),
                    new UserIdentity("steam", "steam-a")));

        Assert.Equal("Acquire rejected.", exception.Message);
        Assert.Equal(1, coordinator.AcquireCount);
        Assert.Equal(0, coordinator.ReleaseCount);
        Assert.Equal(
            new[]
            {
                WorldLifecyclePhase.AcquiringReservation,
                WorldLifecyclePhase.Completed
            },
            observer.Changes.Select(change => change.Phase));
        Assert.All(observer.Changes, change => Assert.Equal(expectedMode, change.Mode));

        var responsibility = observer.Tracker.Current;
        Assert.Equal(WorldLifecycleResponsibilityKind.None, responsibility.Kind);
        Assert.True(responsibility.CanQuitWithoutGuard);
        Assert.True(responsibility.CanSelfUpdate);
    }

    private sealed class TrackingObserver : IWorldLifecycleObserver
    {
        public WorldLifecycleResponsibilityTracker Tracker { get; } = new();
        public List<WorldLifecyclePhaseChange> Changes { get; } = new();

        public void OnPhaseChanged(WorldLifecyclePhaseChange change)
        {
            ArgumentNullException.ThrowIfNull(change);
            Changes.Add(change);
            Tracker.OnPhaseChanged(change);
        }
    }

    private sealed class FailingAcquireCoordinator : IWorldSessionCoordinator
    {
        public int AcquireCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public Task<WorldSession> GetSessionAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorldSession> AcquireHostAsync(
            WorldId worldId,
            UserIdentity user,
            CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            throw new InvalidOperationException("Acquire rejected.");
        }

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

        public Task ReleaseHostAsync(
            WorldId worldId,
            UserIdentity user,
            CancellationToken cancellationToken = default)
        {
            ReleaseCount++;
            throw new InvalidOperationException("Release must not be called after failed acquire.");
        }
    }

    private sealed class EmptyRecoveryStore : IWorkspaceRecoveryStore
    {
        public Task SaveAsync(
            WorkspaceRecoveryRecord record,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RemoveAsync(
            WorkspaceId workspaceId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkspaceRecoveryRecord>> ListAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRecoveryRecord>>(Array.Empty<WorkspaceRecoveryRecord>());
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
