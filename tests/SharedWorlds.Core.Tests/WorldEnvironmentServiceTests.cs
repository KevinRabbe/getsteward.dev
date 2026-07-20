using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorldEnvironmentServiceTests
{
    [Fact]
    public async Task Verify_DelegatesCurrentImmutableEnvironmentToAdapter()
    {
        var storage = new RecordingStorage();
        var adapter = new RecordingAdapter();
        var world = SeedWorld(storage, adapter.Id);
        var originalEnvironmentHead = world.CurrentEnvironmentRevisionId;
        var originalStateHead = world.CurrentStateRevisionId;
        var service = new WorldEnvironmentService(storage);

        var result = await service.VerifyAsync(
            world.Id,
            adapter,
            adapter.Installation);

        Assert.True(result.IsReady);
        Assert.NotNull(adapter.LastVerifiedEnvironment);
        Assert.Equal("7.8.9", adapter.LastVerifiedEnvironment.GameVersion);
        Assert.Equal(originalEnvironmentHead, storage.Worlds[world.Id].CurrentEnvironmentRevisionId);
        Assert.Equal(originalStateHead, storage.Worlds[world.Id].CurrentStateRevisionId);
    }

    private static World SeedWorld(RecordingStorage storage, string adapterId)
    {
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();
        var owner = new UserIdentity("test", "owner", "Owner");
        var world = new World(
            worldId,
            "Ready World",
            adapterId,
            [owner],
            environmentId,
            stateId);
        storage.Worlds[worldId] = world;
        storage.Environments[(worldId, environmentId)] = new EnvironmentRevision(
            environmentId,
            worldId,
            ParentRevisionId: null,
            DateTimeOffset.UtcNow,
            owner,
            new EnvironmentManifest(
                SchemaVersion: 1,
                AdapterId: adapterId,
                GameVersion: "7.8.9",
                Components: [],
                Configuration: new Dictionary<string, string>()));
        return world;
    }

    private sealed class RecordingStorage : IWorldStorage
    {
        public Dictionary<WorldId, World> Worlds { get; } = [];
        public Dictionary<(WorldId WorldId, RevisionId RevisionId), EnvironmentRevision> Environments { get; } = [];

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
            Environments[(revision.WorldId, revision.Id)] = revision;
            return Task.CompletedTask;
        }

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Environments.GetValueOrDefault((worldId, revisionId)));

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

    private sealed class RecordingAdapter : IGameAdapter
    {
        public string Id => "test-adapter";
        public string DisplayName => "Test Adapter";
        public GameAdapterCapabilities Capabilities => GameAdapterCapabilities.None;
        public GameInstallation Installation { get; } = new("test", "/tmp/test", "test");
        public EnvironmentManifest? LastVerifiedEnvironment { get; private set; }

        public Task<EnvironmentVerificationReport> VerifyEnvironmentAsync(
            GameInstallation installation,
            EnvironmentManifest requiredEnvironment,
            CancellationToken cancellationToken = default)
        {
            LastVerifiedEnvironment = requiredEnvironment;
            return Task.FromResult(EnvironmentVerificationReport.Ready());
        }

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
