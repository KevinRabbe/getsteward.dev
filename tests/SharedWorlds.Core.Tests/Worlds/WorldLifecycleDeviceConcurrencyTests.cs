using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Core.Tests.Worlds;

public sealed class WorldLifecycleDeviceConcurrencyTests
{
    [Fact]
    public async Task ActiveManagedWorldBlocksDifferentWorldBeforeCoordinatorAcquire()
    {
        var gate = new ManagedWritableSessionGate();
        var activeWorld = WorldId.New();
        var requestedWorld = WorldId.New();
        using var activeLease = gate.Acquire(activeWorld);
        var coordinator = new CountingCoordinator();
        var service = new WorldLifecycleService(
            new NeverReachedStorage(),
            coordinator,
            new NeverReachedRecoveryStore(),
            gate);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ContinueLocalAsync(
                requestedWorld,
                new NeverReachedAdapter(),
                new GameInstallation("install", "C:/Game", "test"),
                new UserIdentity("steam", "steam-b")));

        Assert.Contains(activeWorld.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, coordinator.AcquireCount);
        Assert.Equal(activeWorld, gate.ActiveWorldId);
    }

    private sealed class CountingCoordinator : IWorldSessionCoordinator
    {
        public int AcquireCount { get; private set; }

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
            throw new InvalidOperationException("Coordinator should not be reached while the device gate is occupied.");
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
            => throw new NotSupportedException();
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

    private sealed class NeverReachedRecoveryStore : IWorkspaceRecoveryStore
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
            => throw new NotSupportedException();
    }

    private sealed class NeverReachedAdapter : IGameAdapter
    {
        public string Id => "test-adapter";
        public string DisplayName => "Test Adapter";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.AutomaticLocalLaunch;

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
