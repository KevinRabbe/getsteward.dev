using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Errors;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Core.Worlds;
using Xunit;

namespace SharedWorlds.Core.Tests;

public sealed class WorldLifecycleSharedEnvironmentVerificationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "steward-shared-environment-gate-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SharedWorldBlockedVerificationStopsBeforeStateOrWorkspacePreparation()
    {
        var fixture = CreateFixture(
            WorldSharingMode.Shared,
            EnvironmentVerificationReport.Blocked(
                new EnvironmentVerificationIssue(
                    "exact-environment-mismatch",
                    "The installed environment does not match the World.")));

        var exception = await Assert.ThrowsAsync<EnvironmentReproductionException>(() =>
            fixture.Lifecycle.PrepareAsync(
                fixture.World.Id,
                fixture.Adapter,
                fixture.Adapter.Installation));

        Assert.Contains("Verify/Repair", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.Adapter.VerifyCount);
        Assert.Equal(0, fixture.Adapter.PrepareCount);
        Assert.Equal(0, fixture.Storage.StateRevisionLoadCount);
        Assert.Equal(0, fixture.Storage.OpenRevisionCount);
    }

    [Fact]
    public async Task SharedWorldReadyVerificationAllowsPreparationAndRestore()
    {
        var fixture = CreateFixture(
            WorldSharingMode.Shared,
            EnvironmentVerificationReport.Ready());

        var prepared = await fixture.Lifecycle.PrepareAsync(
            fixture.World.Id,
            fixture.Adapter,
            fixture.Adapter.Installation);

        Assert.Equal(1, fixture.Adapter.VerifyCount);
        Assert.Equal(1, fixture.Adapter.PrepareCount);
        Assert.Equal(1, fixture.Adapter.RestoreCount);
        Assert.Equal(1, fixture.Storage.StateRevisionLoadCount);
        Assert.Equal(1, fixture.Storage.OpenRevisionCount);
        await fixture.Adapter.FinalizePreparedWorldAsync(
            prepared.PreparedWorld,
            PreparedWorldDisposition.Discard);
    }

    [Fact]
    public async Task LocalOnlyWorldDoesNotRequireSharedEnvironmentVerification()
    {
        var fixture = CreateFixture(
            WorldSharingMode.LocalOnly,
            EnvironmentVerificationReport.Unsupported("Verification intentionally unsupported."));

        var prepared = await fixture.Lifecycle.PrepareAsync(
            fixture.World.Id,
            fixture.Adapter,
            fixture.Adapter.Installation);

        Assert.Equal(0, fixture.Adapter.VerifyCount);
        Assert.Equal(1, fixture.Adapter.PrepareCount);
        Assert.Equal(1, fixture.Adapter.RestoreCount);
        await fixture.Adapter.FinalizePreparedWorldAsync(
            prepared.PreparedWorld,
            PreparedWorldDisposition.Discard);
    }

    private Fixture CreateFixture(
        WorldSharingMode sharingMode,
        EnvironmentVerificationReport verification)
    {
        Directory.CreateDirectory(_root);
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();
        var user = new UserIdentity("steam", "76561198000000001", "Tester");
        var manifest = new EnvironmentManifest(
            1,
            "test",
            "1.0.0",
            [],
            new Dictionary<string, string>());
        var world = new World(
            worldId,
            "Shared Test World",
            "test",
            [user],
            environmentId,
            stateId)
        {
            SharingMode = sharingMode
        };
        var storage = new RecordingStorage(
            world,
            new EnvironmentRevision(
                environmentId,
                worldId,
                null,
                DateTimeOffset.UtcNow,
                user,
                manifest),
            new StateRevision(
                stateId,
                worldId,
                null,
                DateTimeOffset.UtcNow,
                user,
                "test",
                "state-package"),
            Encoding.UTF8.GetBytes("state"));
        var adapter = new VerificationAdapter(_root, verification);
        var lifecycle = new WorldLifecycleService(
            storage,
            new NoopCoordinator(),
            new EmptyRecoveryStore());
        return new Fixture(world, storage, adapter, lifecycle);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed record Fixture(
        World World,
        RecordingStorage Storage,
        VerificationAdapter Adapter,
        WorldLifecycleService Lifecycle);

    private sealed class VerificationAdapter : IGameAdapter
    {
        private readonly string _root;
        private readonly EnvironmentVerificationReport _verification;

        public VerificationAdapter(string root, EnvironmentVerificationReport verification)
        {
            _root = root;
            _verification = verification;
            Installation = new GameInstallation("test", root, "test");
        }

        public string Id => "test";
        public string DisplayName => "Test Adapter";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.AutomaticLocalLaunch;
        public GameInstallation Installation { get; }
        public int VerifyCount { get; private set; }
        public int PrepareCount { get; private set; }
        public int RestoreCount { get; private set; }

        public Task<IReadOnlyList<GameInstallation>> DiscoverInstallationsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameInstallation>>([Installation]);

        public Task<IReadOnlyList<DetectedWorld>> DiscoverWorldsAsync(
            GameInstallation installation,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DetectedWorld>>([]);

        public Task<EnvironmentManifest> InspectEnvironmentAsync(
            GameInstallation installation,
            DetectedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EnvironmentVerificationReport> VerifyEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            CancellationToken cancellationToken = default)
        {
            VerifyCount++;
            return Task.FromResult(_verification);
        }

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
            PrepareCount++;
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
        {
            RestoreCount++;
            return Task.CompletedTask;
        }

        public Task<CapturedState> CaptureStateAsync(
            PreparedWorld world,
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
        {
            if (disposition == PreparedWorldDisposition.Discard && Directory.Exists(world.WorkingDirectory))
            {
                Directory.Delete(world.WorkingDirectory, recursive: true);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStorage : IWorldStorage
    {
        private readonly World _world;
        private readonly EnvironmentRevision _environment;
        private readonly StateRevision _state;
        private readonly byte[] _package;

        public RecordingStorage(
            World world,
            EnvironmentRevision environment,
            StateRevision state,
            byte[] package)
        {
            _world = world;
            _environment = environment;
            _state = state;
            _package = package;
        }

        public int StateRevisionLoadCount { get; private set; }
        public int OpenRevisionCount { get; private set; }

        public Task<World?> LoadWorldAsync(WorldId worldId, CancellationToken cancellationToken = default)
            => Task.FromResult<World?>(_world.Id == worldId ? _world : null);

        public Task<IReadOnlyList<World>> ListWorldsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<World>>([_world]);

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

        public Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            StateRevisionLoadCount++;
            return Task.FromResult<StateRevision?>(
                _state.WorldId == worldId && _state.Id == revisionId
                    ? _state
                    : null);
        }

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            OpenRevisionCount++;
            return Task.FromResult<Stream>(new MemoryStream(_package, writable: false));
        }

        public Task SaveWorldAsync(World world, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class NoopCoordinator : IWorldSessionCoordinator
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
            => throw new NotSupportedException();

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

    private sealed class EmptyRecoveryStore : IWorkspaceRecoveryStore
    {
        public Task SaveAsync(
            WorkspaceRecoveryRecord record,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveAsync(
            WorkspaceId workspaceId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<WorkspaceRecoveryRecord>> ListAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRecoveryRecord>>([]);
    }
}
