using System.Net;
using System.Security.Cryptography;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Remote;
using SharedWorlds.Infrastructure.Storage;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class StewardOwnedPrivateWorldMaterializationServiceTests
{
    private static readonly UserIdentity LocalOwner = new(
        "steam",
        "target-owner",
        "Target Owner");
    private static readonly UserIdentity SourceActor = new(
        "steam",
        "source-owner",
        "Source Owner");

    [Fact]
    public async Task MaterializesExactIdentityAndPublishesWorldLast()
    {
        using var fixture = await Fixture.CreateAsync(seedCache: true);
        var recording = new RecordingWorldStorage(
            new LocalWorldStorage(fixture.StorageRoot));
        var service = new StewardOwnedPrivateWorldMaterializationService(
            recording,
            fixture.Cache);

        var result = await service.MaterializeAsync(
            fixture.Catalog,
            fixture.Prepared,
            LocalOwner);

        Assert.Equal(OwnedPrivateWorldMaterializationStatus.Materialized, result.Status);
        Assert.Equal(fixture.WorldId, result.World.Id);
        Assert.Equal("Factory World", result.World.Name);
        Assert.Equal("factorio", result.World.GameAdapterId);
        Assert.Equal(LocalOwner, Assert.Single(result.World.Members));
        Assert.Equal(fixture.Environment.Id, result.World.CurrentEnvironmentRevisionId);
        Assert.Equal(fixture.State.Id, result.World.CurrentStateRevisionId);
        Assert.Equal(WorldSharingMode.LocalOnly, result.World.SharingMode);
        Assert.Equal(WorldVisibility.Private, result.World.Visibility);
        Assert.Null(result.World.StartedFrom);
        Assert.Equal(
            ["StoreEnvironment", "StoreRevision", "SaveWorld"],
            recording.Operations);
        Assert.Equal(1, fixture.DirectTransferCalls);

        var storedWorld = Assert.IsType<World>(
            await recording.LoadWorldAsync(fixture.WorldId));
        Assert.Equal(result.World.Id, storedWorld.Id);
        Assert.Equal(result.World.Name, storedWorld.Name);
        Assert.Equal(result.World.GameAdapterId, storedWorld.GameAdapterId);
        Assert.Equal(LocalOwner, Assert.Single(storedWorld.Members));
        Assert.Equal(
            result.World.CurrentEnvironmentRevisionId,
            storedWorld.CurrentEnvironmentRevisionId);
        Assert.Equal(
            result.World.CurrentStateRevisionId,
            storedWorld.CurrentStateRevisionId);
        Assert.Equal(result.World.SharingMode, storedWorld.SharingMode);
        Assert.Equal(result.World.Visibility, storedWorld.Visibility);
        Assert.Null(storedWorld.StartedFrom);

        var storedEnvironment = Assert.IsType<EnvironmentRevision>(
            await recording.LoadEnvironmentRevisionAsync(
                fixture.WorldId,
                fixture.Environment.Id));
        Assert.Equal(fixture.Environment.Id, storedEnvironment.Id);
        Assert.Equal(fixture.Environment.WorldId, storedEnvironment.WorldId);
        Assert.Equal(fixture.Environment.ParentRevisionId, storedEnvironment.ParentRevisionId);
        Assert.Equal(fixture.Environment.CreatedAt, storedEnvironment.CreatedAt);
        Assert.Equal(SourceActor, storedEnvironment.CreatedBy);
        Assert.Equal(fixture.Manifest.AdapterId, storedEnvironment.Manifest.AdapterId);
        Assert.Equal(fixture.Manifest.GameVersion, storedEnvironment.Manifest.GameVersion);

        var storedState = Assert.IsType<StateRevision>(
            await recording.LoadStateRevisionAsync(
                fixture.WorldId,
                fixture.State.Id));
        Assert.Equal(fixture.State, storedState);
        Assert.Equal(SourceActor, storedState.CreatedBy);
        await using var payload = await recording.OpenRevisionAsync(
            fixture.WorldId,
            fixture.State.Id);
        await using var copied = new MemoryStream();
        await payload.CopyToAsync(copied);
        Assert.Equal(fixture.Bytes, copied.ToArray());
    }

    [Fact]
    public async Task ExactRetryReturnsAlreadyMaterializedWithoutAnotherWriteOrDownload()
    {
        using var fixture = await Fixture.CreateAsync(seedCache: true);
        var recording = new RecordingWorldStorage(
            new LocalWorldStorage(fixture.StorageRoot));
        var service = new StewardOwnedPrivateWorldMaterializationService(
            recording,
            fixture.Cache);

        var first = await service.MaterializeAsync(
            fixture.Catalog,
            fixture.Prepared,
            LocalOwner);
        var operationCount = recording.Operations.Count;
        var second = await service.MaterializeAsync(
            fixture.Catalog,
            fixture.Prepared,
            LocalOwner);

        Assert.Equal(OwnedPrivateWorldMaterializationStatus.Materialized, first.Status);
        Assert.Equal(OwnedPrivateWorldMaterializationStatus.AlreadyMaterialized, second.Status);
        Assert.Equal(operationCount, recording.Operations.Count);
        Assert.Equal(1, fixture.DirectTransferCalls);
    }

    [Fact]
    public async Task ConcurrentExactCallsConvergeToOneMaterialization()
    {
        using var fixture = await Fixture.CreateAsync(seedCache: true);
        var recording = new RecordingWorldStorage(
            new LocalWorldStorage(fixture.StorageRoot));
        var service = new StewardOwnedPrivateWorldMaterializationService(
            recording,
            fixture.Cache);

        var results = await Task.WhenAll(
            service.MaterializeAsync(fixture.Catalog, fixture.Prepared, LocalOwner),
            service.MaterializeAsync(fixture.Catalog, fixture.Prepared, LocalOwner));

        Assert.Contains(
            results,
            result => result.Status == OwnedPrivateWorldMaterializationStatus.Materialized);
        Assert.Contains(
            results,
            result => result.Status == OwnedPrivateWorldMaterializationStatus.AlreadyMaterialized);
        Assert.Equal(1, recording.Operations.Count(value => value == "SaveWorld"));
        Assert.Equal(1, fixture.DirectTransferCalls);
    }

    [Fact]
    public async Task ExactPartialRevisionsConvergeWithoutCacheAccess()
    {
        using var fixture = await Fixture.CreateAsync(seedCache: false);
        var storage = new LocalWorldStorage(fixture.StorageRoot);
        await storage.StoreEnvironmentRevisionAsync(fixture.Environment);
        await using (var state = new MemoryStream(fixture.Bytes, writable: false))
        {
            await storage.StoreRevisionAsync(fixture.State, state);
        }

        var recording = new RecordingWorldStorage(storage);
        var service = new StewardOwnedPrivateWorldMaterializationService(
            recording,
            fixture.Cache);

        var result = await service.MaterializeAsync(
            fixture.Catalog,
            fixture.Prepared,
            LocalOwner);

        Assert.Equal(OwnedPrivateWorldMaterializationStatus.Materialized, result.Status);
        Assert.Equal(["SaveWorld"], recording.Operations);
        Assert.Equal(0, fixture.DirectTransferCalls);
        Assert.Single(await storage.ListWorldsAsync());
    }

    [Fact]
    public async Task CatalogAndPreparedHeadMismatchFailsBeforeStorageOrTransfer()
    {
        using var fixture = await Fixture.CreateAsync(seedCache: false);
        var recording = new RecordingWorldStorage(
            new LocalWorldStorage(fixture.StorageRoot));
        var service = new StewardOwnedPrivateWorldMaterializationService(
            recording,
            fixture.Cache);
        var mismatchedSource = fixture.Catalog.Source! with
        {
            StateRevisionId = RevisionId.New()
        };
        var mismatchedCatalog = fixture.Catalog with { Source = mismatchedSource };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.MaterializeAsync(
                mismatchedCatalog,
                fixture.Prepared,
                LocalOwner));

        Assert.Empty(recording.Operations);
        Assert.Empty(await recording.ListWorldsAsync());
        Assert.Equal(0, fixture.DirectTransferCalls);
    }

    [Fact]
    public async Task ExistingWorldConflictIsNeverOverwritten()
    {
        using var fixture = await Fixture.CreateAsync(seedCache: false);
        var storage = new LocalWorldStorage(fixture.StorageRoot);
        var conflicting = new World(
            fixture.WorldId,
            "Different Local World",
            "factorio",
            Members: [LocalOwner],
            CurrentEnvironmentRevisionId: fixture.Environment.Id,
            CurrentStateRevisionId: fixture.State.Id);
        await storage.SaveWorldAsync(conflicting);
        var recording = new RecordingWorldStorage(storage);
        var service = new StewardOwnedPrivateWorldMaterializationService(
            recording,
            fixture.Cache);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.MaterializeAsync(
                fixture.Catalog,
                fixture.Prepared,
                LocalOwner));

        Assert.Empty(recording.Operations);
        var persisted = Assert.IsType<World>(
            await storage.LoadWorldAsync(fixture.WorldId));
        Assert.Equal(conflicting.Id, persisted.Id);
        Assert.Equal(conflicting.Name, persisted.Name);
        Assert.Equal(conflicting.GameAdapterId, persisted.GameAdapterId);
        Assert.Equal(LocalOwner, Assert.Single(persisted.Members));
        Assert.Equal(
            conflicting.CurrentEnvironmentRevisionId,
            persisted.CurrentEnvironmentRevisionId);
        Assert.Equal(
            conflicting.CurrentStateRevisionId,
            persisted.CurrentStateRevisionId);
        Assert.Equal(0, fixture.DirectTransferCalls);
    }

    [Fact]
    public async Task SameEnvironmentIdWithDifferentMetadataFailsBeforeStateOrWorldWrite()
    {
        using var fixture = await Fixture.CreateAsync(seedCache: false);
        var storage = new LocalWorldStorage(fixture.StorageRoot);
        var conflicting = fixture.Environment with
        {
            Manifest = fixture.Manifest with { GameVersion = "different-version" }
        };
        await storage.StoreEnvironmentRevisionAsync(conflicting);
        var recording = new RecordingWorldStorage(storage);
        var service = new StewardOwnedPrivateWorldMaterializationService(
            recording,
            fixture.Cache);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.MaterializeAsync(
                fixture.Catalog,
                fixture.Prepared,
                LocalOwner));

        Assert.Empty(recording.Operations);
        Assert.Null(await storage.LoadStateRevisionAsync(fixture.WorldId, fixture.State.Id));
        Assert.Empty(await storage.ListWorldsAsync());
        Assert.Equal(0, fixture.DirectTransferCalls);
    }

    [Fact]
    public async Task SameStateIdWithDifferentMetadataFailsBeforeWorldWrite()
    {
        using var fixture = await Fixture.CreateAsync(seedCache: false);
        var storage = new LocalWorldStorage(fixture.StorageRoot);
        await storage.StoreEnvironmentRevisionAsync(fixture.Environment);
        var conflicting = fixture.State with { StatePackageId = "different-package-id" };
        await using (var state = new MemoryStream(fixture.Bytes, writable: false))
        {
            await storage.StoreRevisionAsync(conflicting, state);
        }

        var recording = new RecordingWorldStorage(storage);
        var service = new StewardOwnedPrivateWorldMaterializationService(
            recording,
            fixture.Cache);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.MaterializeAsync(
                fixture.Catalog,
                fixture.Prepared,
                LocalOwner));

        Assert.Empty(recording.Operations);
        Assert.Empty(await storage.ListWorldsAsync());
        Assert.Equal(0, fixture.DirectTransferCalls);
    }

    [Fact]
    public async Task CorruptDownloadFailsBeforeAnyLocalMutation()
    {
        using var fixture = await Fixture.CreateAsync(
            seedCache: false,
            transferBytes: "xxxxxxxxxxxxxxxxxxxxxxxxx"u8.ToArray());
        var recording = new RecordingWorldStorage(
            new LocalWorldStorage(fixture.StorageRoot));
        var service = new StewardOwnedPrivateWorldMaterializationService(
            recording,
            fixture.Cache);

        await Assert.ThrowsAsync<PackageIntegrityException>(() =>
            service.MaterializeAsync(
                fixture.Catalog,
                fixture.Prepared,
                LocalOwner));

        Assert.Empty(recording.Operations);
        Assert.Null(await recording.LoadEnvironmentRevisionAsync(
            fixture.WorldId,
            fixture.Environment.Id));
        Assert.Null(await recording.LoadStateRevisionAsync(
            fixture.WorldId,
            fixture.State.Id));
        Assert.Empty(await recording.ListWorldsAsync());
        Assert.Equal(1, fixture.DirectTransferCalls);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _transferClient;
        private readonly TransferCounter _counter;
        private readonly string _root;

        private Fixture(
            string root,
            HttpClient transferClient,
            TransferCounter counter,
            VerifiedPackageCache cache,
            WorldId worldId,
            EnvironmentManifest manifest,
            EnvironmentRevision environment,
            StateRevision state,
            byte[] bytes,
            StewardOwnedPrivateWorldCatalogEntry catalog,
            RemoteVerifiedPrivateSnapshotMaterialization prepared)
        {
            _root = root;
            _transferClient = transferClient;
            _counter = counter;
            Cache = cache;
            WorldId = worldId;
            Manifest = manifest;
            Environment = environment;
            State = state;
            Bytes = bytes;
            Catalog = catalog;
            Prepared = prepared;
        }

        public string StorageRoot => Path.Combine(_root, "storage");
        public VerifiedPackageCache Cache { get; }
        public WorldId WorldId { get; }
        public EnvironmentManifest Manifest { get; }
        public EnvironmentRevision Environment { get; }
        public StateRevision State { get; }
        public byte[] Bytes { get; }
        public StewardOwnedPrivateWorldCatalogEntry Catalog { get; }
        public RemoteVerifiedPrivateSnapshotMaterialization Prepared { get; }
        public int DirectTransferCalls => _counter.Count;

        public static async Task<Fixture> CreateAsync(
            bool seedCache,
            byte[]? transferBytes = null)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "steward-private-world-materialization-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "storage"));

            var worldId = WorldId.New();
            var environmentId = RevisionId.New();
            var manifest = new EnvironmentManifest(
                SchemaVersion: 1,
                AdapterId: "factorio",
                GameVersion: "2.0.0",
                Components: [],
                Configuration: new Dictionary<string, string>());
            var environment = new EnvironmentRevision(
                environmentId,
                worldId,
                ParentRevisionId: RevisionId.New(),
                CreatedAt: new DateTimeOffset(2026, 8, 4, 1, 0, 0, TimeSpan.Zero),
                CreatedBy: SourceActor,
                manifest);
            var state = new StateRevision(
                RevisionId.New(),
                worldId,
                ParentRevisionId: RevisionId.New(),
                CreatedAt: new DateTimeOffset(2026, 8, 4, 1, 1, 0, TimeSpan.Zero),
                CreatedBy: SourceActor,
                AdapterId: "factorio",
                StatePackageId: "source-state-package-id",
                EnvironmentRevisionId: environmentId);
            var bytes = "exact-private-world-state"u8.ToArray();
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
            var authorization = new AuthorizedPackageDownload(
                new Uri("https://objects.example/private-world"),
                new Dictionary<string, string>(),
                DateTimeOffset.UtcNow.AddHours(1),
                bytes.LongLength,
                sha256);
            var counter = new TransferCounter();
            var responseBytes = transferBytes ?? bytes;
            var transferClient = new HttpClient(new DelegateHandler(_ =>
            {
                counter.Count++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(responseBytes)
                });
            }));
            var cache = new VerifiedPackageCache(
                Path.Combine(root, "cache"),
                transferClient,
                new VerifiedPackageCacheOptions(
                    minimumFreeSpaceReserveBytes: 0,
                    copyBufferBytes: 64 * 1024,
                    maximumCacheBytes: 1024 * 1024,
                    transferInactivityTimeout: TimeSpan.FromSeconds(5)));
            var cached = seedCache
                ? await cache.EnsureAsync(authorization)
                : new VerifiedCachedPackage(
                    Path.Combine(root, "prepared.package"),
                    bytes.LongLength,
                    sha256);
            var source = new StewardOwnedWorldLocation(
                worldId,
                "pc-a",
                state.Id,
                environment.Id,
                DateTimeOffset.UtcNow,
                new StewardOwnedWorldPresentation("Factory World", "factorio"));
            var catalog = new StewardOwnedPrivateWorldCatalogEntry(
                worldId,
                "Factory World",
                "factorio",
                BringHereAvailability.Available,
                source,
                ConflictingClaims: [],
                Reason: "One exact source is available.");
            var plan = new RemotePrivateSnapshotMaterializationPlan(
                worldId,
                "pc-a",
                state.Id,
                environment.Id,
                "factorio",
                bytes.LongLength,
                sha256,
                manifest,
                state,
                environment,
                authorization);
            var prepared = new RemoteVerifiedPrivateSnapshotMaterialization(
                plan,
                cached);
            return new(
                root,
                transferClient,
                counter,
                cache,
                worldId,
                manifest,
                environment,
                state,
                bytes,
                catalog,
                prepared);
        }

        public void Dispose()
        {
            _transferClient.Dispose();
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
    }

    private sealed class RecordingWorldStorage : IWorldStorage
    {
        private readonly IWorldStorage _inner;

        public RecordingWorldStorage(IWorldStorage inner)
        {
            _inner = inner;
        }

        public List<string> Operations { get; } = [];

        public Task<IReadOnlyList<World>> ListWorldsAsync(
            CancellationToken cancellationToken = default)
            => _inner.ListWorldsAsync(cancellationToken);

        public async Task SaveWorldAsync(
            World world,
            CancellationToken cancellationToken = default)
        {
            Operations.Add("SaveWorld");
            await _inner.SaveWorldAsync(world, cancellationToken);
        }

        public Task<World?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => _inner.LoadWorldAsync(worldId, cancellationToken);

        public Task<bool> DeleteWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => _inner.DeleteWorldAsync(worldId, cancellationToken);

        public async Task StoreEnvironmentRevisionAsync(
            EnvironmentRevision revision,
            CancellationToken cancellationToken = default)
        {
            Operations.Add("StoreEnvironment");
            await _inner.StoreEnvironmentRevisionAsync(revision, cancellationToken);
        }

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => _inner.LoadEnvironmentRevisionAsync(worldId, revisionId, cancellationToken);

        public async Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
        {
            Operations.Add("StoreRevision");
            await _inner.StoreRevisionAsync(revision, package, cancellationToken);
        }

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => _inner.LoadStateRevisionAsync(worldId, revisionId, cancellationToken);

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => _inner.OpenRevisionAsync(worldId, revisionId, cancellationToken);

        public Task<bool> IsRevisionPayloadAvailableAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => _inner.IsRevisionPayloadAvailableAsync(
                worldId,
                revisionId,
                cancellationToken);

        public Task<long?> GetRevisionPayloadSizeAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => _inner.GetRevisionPayloadSizeAsync(
                worldId,
                revisionId,
                cancellationToken);

        public Task<bool> EvictRevisionPayloadAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => _inner.EvictRevisionPayloadAsync(
                worldId,
                revisionId,
                cancellationToken);
    }

    private sealed class TransferCounter
    {
        public int Count;
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public DelegateHandler(
            Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request);
    }
}
