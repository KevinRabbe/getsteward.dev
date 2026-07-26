using System.Text;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Errors;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorldCreationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-world-creation-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreatePersistsNativeEnvironmentAndStateThenCanonicalWorld()
    {
        var storage = new RecordingStorage();
        var adapter = new NativeCreationAdapter(_root);
        var service = new WorldCreationService(storage);
        var owner = TestUser();

        var world = await service.CreateAsync(
            adapter,
            adapter.Installation,
            new WorldCreationRequest("Our Factory"),
            owner);

        Assert.Equal("Our Factory", world.Name);
        Assert.Equal(adapter.Id, world.GameAdapterId);
        Assert.Equal(WorldSharingMode.LocalOnly, world.SharingMode);
        Assert.Equal([owner], world.Members);
        Assert.Single(storage.EnvironmentRevisions);
        Assert.Single(storage.StateRevisions);
        Assert.Single(storage.Worlds);
        Assert.Equal("created-state", Encoding.UTF8.GetString(Assert.Single(storage.StatePayloads).Value));
        Assert.NotNull(adapter.LastCapturedPackagePath);
        Assert.False(File.Exists(adapter.LastCapturedPackagePath));
    }

    [Fact]
    public async Task CreateDoesNotPublishWorldWhenInitialStateStorageFails()
    {
        var storage = new RecordingStorage { FailStoreRevision = true };
        var adapter = new NativeCreationAdapter(_root);
        var service = new WorldCreationService(storage);

        await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(
            adapter,
            adapter.Installation,
            new WorldCreationRequest("Our Factory"),
            TestUser()));

        Assert.Empty(storage.Worlds);
        Assert.NotNull(adapter.LastCapturedPackagePath);
        Assert.False(File.Exists(adapter.LastCapturedPackagePath));
    }

    [Fact]
    public async Task CreateRejectsAdapterWithoutNativeCreationCapabilityBeforeCallingIt()
    {
        var adapter = new NativeCreationAdapter(_root, advertiseCreation: false);
        var service = new WorldCreationService(new RecordingStorage());

        await Assert.ThrowsAsync<NotSupportedException>(() => service.CreateAsync(
            adapter,
            adapter.Installation,
            new WorldCreationRequest("Our Factory"),
            TestUser()));

        Assert.False(adapter.CreateCalled);
    }

    [Fact]
    public async Task CreateRejectsMismatchedNativeEnvironmentAndCleansCapture()
    {
        var storage = new RecordingStorage();
        var adapter = new NativeCreationAdapter(_root, returnedAdapterId: "wrong-adapter");
        var service = new WorldCreationService(storage);

        await Assert.ThrowsAsync<AdapterMismatchException>(() => service.CreateAsync(
            adapter,
            adapter.Installation,
            new WorldCreationRequest("Our Factory"),
            TestUser()));

        Assert.Empty(storage.Worlds);
        Assert.Empty(storage.EnvironmentRevisions);
        Assert.Empty(storage.StateRevisions);
        Assert.NotNull(adapter.LastCapturedPackagePath);
        Assert.False(File.Exists(adapter.LastCapturedPackagePath));
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class NativeCreationAdapter : IGameAdapter
    {
        private readonly string _root;
        private readonly bool _advertiseCreation;
        private readonly string _returnedAdapterId;

        public NativeCreationAdapter(
            string root,
            bool advertiseCreation = true,
            string returnedAdapterId = "native-test")
        {
            _root = root;
            _advertiseCreation = advertiseCreation;
            _returnedAdapterId = returnedAdapterId;
            Directory.CreateDirectory(root);
            Installation = new GameInstallation("native-test-install", root, "test");
        }

        public string Id => "native-test";
        public string DisplayName => "Native Test";
        public GameAdapterCapabilities Capabilities => _advertiseCreation
            ? GameAdapterCapabilities.NativeWorldCreation
            : GameAdapterCapabilities.None;
        public GameInstallation Installation { get; }
        public bool CreateCalled { get; private set; }
        public string? LastCapturedPackagePath { get; private set; }

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
            => throw new NotSupportedException();

        public Task<CapturedState> CaptureDetectedWorldAsync(
            GameInstallation installation,
            DetectedWorld world,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<NativeWorldCreationResult> CreateWorldAsync(
            GameInstallation installation,
            WorldCreationRequest request,
            CancellationToken cancellationToken = default)
        {
            CreateCalled = true;
            var path = Path.Combine(_root, $"created-{Guid.NewGuid():N}.package");
            File.WriteAllText(path, "created-state");
            LastCapturedPackagePath = path;

            var environment = new EnvironmentManifest(
                1,
                _returnedAdapterId,
                "1.0.0",
                [],
                new Dictionary<string, string>());
            var state = new CapturedState(
                new StatePackage(Path.GetFileNameWithoutExtension(path), path),
                DateTimeOffset.UtcNow,
                DeletePackageAfterStore: true);
            return Task.FromResult(new NativeWorldCreationResult(environment, state));
        }

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

        public Task FinalizePreparedWorldAsync(
            PreparedWorld world,
            PreparedWorldDisposition disposition,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingStorage : IWorldStorage
    {
        public Dictionary<WorldId, World> Worlds { get; } = [];
        public Dictionary<(WorldId, RevisionId), EnvironmentRevision> EnvironmentRevisions { get; } = [];
        public Dictionary<(WorldId, RevisionId), StateRevision> StateRevisions { get; } = [];
        public Dictionary<(WorldId, RevisionId), byte[]> StatePayloads { get; } = [];
        public bool FailStoreRevision { get; set; }

        public Task SaveWorldAsync(World world, CancellationToken cancellationToken = default)
        {
            Worlds[world.Id] = world;
            return Task.CompletedTask;
        }

        public Task<World?> LoadWorldAsync(WorldId worldId, CancellationToken cancellationToken = default)
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
                throw new IOException("Injected state storage failure.");
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
