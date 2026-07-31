using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Errors;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorldLifecycleStateEnvironmentGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"safe-world-state-environment-guard-{Guid.NewGuid():N}");

    [Fact]
    public async Task Prepare_AllowsLinkedCurrentStateAndEnvironmentPair()
    {
        var fixture = CreateFixture(linkState: true, environmentChanged: false);

        var context = await fixture.Lifecycle.PrepareAsync(
            fixture.World.Id,
            fixture.Adapter,
            fixture.Adapter.Installation);

        Assert.Equal(fixture.World.Id, context.World.Id);
        Assert.True(fixture.Adapter.PrepareCalled);
        Assert.True(fixture.Adapter.RestoreCalled);
    }

    [Fact]
    public async Task Prepare_RejectsLinkedStateEnvironmentMismatchBeforeAdapterPreparation()
    {
        var fixture = CreateFixture(
            linkState: true,
            environmentChanged: false,
            linkedEnvironmentOverride: RevisionId.New());

        var exception = await Assert.ThrowsAsync<WorldIntegrityException>(() =>
            fixture.Lifecycle.PrepareAsync(
                fixture.World.Id,
                fixture.Adapter,
                fixture.Adapter.Installation));

        Assert.Equal(fixture.World.Id, exception.WorldId);
        Assert.Contains("belongs to environment", exception.Message, StringComparison.Ordinal);
        Assert.False(fixture.Adapter.PrepareCalled);
        Assert.False(fixture.Adapter.RestoreCalled);
    }

    [Fact]
    public async Task Prepare_AllowsLegacyUnlinkedStateWhileEnvironmentIsOriginalRoot()
    {
        var fixture = CreateFixture(linkState: false, environmentChanged: false);

        await fixture.Lifecycle.PrepareAsync(
            fixture.World.Id,
            fixture.Adapter,
            fixture.Adapter.Installation);

        Assert.True(fixture.Adapter.PrepareCalled);
        Assert.True(fixture.Adapter.RestoreCalled);
    }

    [Fact]
    public async Task Prepare_RejectsLegacyUnlinkedStateAfterEnvironmentChangedBeforeAdapterPreparation()
    {
        var fixture = CreateFixture(linkState: false, environmentChanged: true);

        var exception = await Assert.ThrowsAsync<WorldIntegrityException>(() =>
            fixture.Lifecycle.PrepareAsync(
                fixture.World.Id,
                fixture.Adapter,
                fixture.Adapter.Installation));

        Assert.Equal(fixture.World.Id, exception.WorldId);
        Assert.Contains("predates exact environment linking", exception.Message, StringComparison.Ordinal);
        Assert.Contains("will not guess", exception.Message, StringComparison.Ordinal);
        Assert.False(fixture.Adapter.PrepareCalled);
        Assert.False(fixture.Adapter.RestoreCalled);
    }

    [Fact]
    public async Task Prepare_RejectsCurrentStateMetadataOwnedByDifferentWorld()
    {
        var fixture = CreateFixture(
            linkState: true,
            environmentChanged: false,
            stateWorldOverride: WorldId.New());

        var exception = await Assert.ThrowsAsync<WorldIntegrityException>(() =>
            fixture.Lifecycle.PrepareAsync(
                fixture.World.Id,
                fixture.Adapter,
                fixture.Adapter.Installation));

        Assert.Equal(fixture.World.Id, exception.WorldId);
        Assert.Contains("state revision belongs to a different World", exception.Message, StringComparison.Ordinal);
        Assert.False(fixture.Adapter.PrepareCalled);
    }

    [Fact]
    public async Task Prepare_RejectsCurrentEnvironmentMetadataOwnedByDifferentWorld()
    {
        var fixture = CreateFixture(
            linkState: true,
            environmentChanged: false,
            environmentWorldOverride: WorldId.New());

        var exception = await Assert.ThrowsAsync<WorldIntegrityException>(() =>
            fixture.Lifecycle.PrepareAsync(
                fixture.World.Id,
                fixture.Adapter,
                fixture.Adapter.Installation));

        Assert.Equal(fixture.World.Id, exception.WorldId);
        Assert.Contains("environment revision belongs to a different World", exception.Message, StringComparison.Ordinal);
        Assert.False(fixture.Adapter.PrepareCalled);
    }

    private Fixture CreateFixture(
        bool linkState,
        bool environmentChanged,
        RevisionId? linkedEnvironmentOverride = null,
        WorldId? stateWorldOverride = null,
        WorldId? environmentWorldOverride = null)
    {
        Directory.CreateDirectory(_root);
        var storage = new MemoryWorldStorage();
        var adapter = new GuardAdapter(_root);
        var owner = new UserIdentity("local", "tester", "Tester");
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();

        var environment = new EnvironmentRevision(
            environmentId,
            environmentWorldOverride ?? worldId,
            ParentRevisionId: environmentChanged ? RevisionId.New() : null,
            DateTimeOffset.UtcNow,
            owner,
            adapter.Manifest);
        var state = new StateRevision(
            stateId,
            stateWorldOverride ?? worldId,
            ParentRevisionId: null,
            DateTimeOffset.UtcNow,
            owner,
            adapter.Id,
            "guard-state",
            EnvironmentRevisionId: linkState
                ? linkedEnvironmentOverride ?? environmentId
                : null);
        var world = new World(
            worldId,
            "Guard World",
            adapter.Id,
            [owner],
            environmentId,
            stateId)
        {
            SharingMode = WorldSharingMode.LocalOnly
        };

        storage.Worlds[worldId] = world;
        storage.EnvironmentRevisions[(worldId, environmentId)] = environment;
        storage.StateRevisions[(worldId, stateId)] = state;
        storage.StatePayloads[(worldId, stateId)] = [1, 2, 3];

        var lifecycle = new WorldLifecycleService(
            storage,
            new NoOpSessionCoordinator(),
            new NoOpWorkspaceRecoveryStore());
        return new Fixture(world, adapter, lifecycle);
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
            // Test cleanup must not hide the actual assertion result.
        }
        catch (UnauthorizedAccessException)
        {
            // Test cleanup must not hide the actual assertion result.
        }
    }

    private sealed record Fixture(
        World World,
        GuardAdapter Adapter,
        WorldLifecycleService Lifecycle);

    private sealed class GuardAdapter : IGameAdapter
    {
        private readonly string _root;

        public GuardAdapter(string root)
        {
            _root = root;
            Installation = new GameInstallation("guard-installation", root, "test");
            Manifest = new EnvironmentManifest(
                1,
                Id,
                "1.0.0",
                [],
                new Dictionary<string, string>());
        }

        public string Id => "guard-adapter";
        public string DisplayName => "Guard Adapter";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.AutomaticLocalLaunch;
        public GameInstallation Installation { get; }
        public EnvironmentManifest Manifest { get; }
        public bool PrepareCalled { get; private set; }
        public bool RestoreCalled { get; private set; }

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

        public Task<EnvironmentVerificationReport> VerifyEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            CancellationToken cancellationToken = default)
            => Task.FromResult(EnvironmentVerificationReport.Ready());

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
            PrepareCalled = true;
            var path = Path.Combine(_root, $"prepared-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return Task.FromResult(new PreparedWorld(installation, path, requiredEnvironment));
        }

        public Task<CapturedState> CaptureStateAsync(
            PreparedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RestoreStateAsync(
            PreparedWorld world,
            StatePackage state,
            CancellationToken cancellationToken = default)
        {
            RestoreCalled = true;
            return Task.CompletedTask;
        }

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
            => Task.CompletedTask;

        public Task FinalizePreparedWorldAsync(
            PreparedWorld world,
            PreparedWorldDisposition disposition,
            CancellationToken cancellationToken = default)
        {
            if (Directory.Exists(world.WorkingDirectory))
            {
                Directory.Delete(world.WorkingDirectory, recursive: true);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class NoOpSessionCoordinator : IWorldSessionCoordinator
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
            => Task.CompletedTask;
    }

    private sealed class NoOpWorkspaceRecoveryStore : IWorkspaceRecoveryStore
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

    private sealed class MemoryWorldStorage : IWorldStorage
    {
        public Dictionary<WorldId, World> Worlds { get; } = [];
        public Dictionary<(WorldId, RevisionId), EnvironmentRevision> EnvironmentRevisions { get; } = [];
        public Dictionary<(WorldId, RevisionId), StateRevision> StateRevisions { get; } = [];
        public Dictionary<(WorldId, RevisionId), byte[]> StatePayloads { get; } = [];

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
            await using var memory = new MemoryStream();
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
