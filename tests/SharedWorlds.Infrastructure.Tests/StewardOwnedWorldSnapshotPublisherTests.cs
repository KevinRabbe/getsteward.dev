using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Remote;
using SharedWorlds.Infrastructure.Storage;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class StewardOwnedWorldSnapshotPublisherTests : IDisposable
{
    private const string AdapterId = "test.adapter";

    private static readonly DateTimeOffset ObservedAt =
        new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-owned-snapshot-publisher-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task PublishesExactCanonicalIdentityManifestAndBytes()
    {
        var storage = CreateStorage();
        var fixture = await StoreCanonicalWorldAsync(storage, new byte[] { 1, 2, 3, 4 });
        PublishedSnapshot? published = null;
        var publisher = new StewardOwnedWorldSnapshotPublisher(
            storage,
            async (
                worldId,
                stateId,
                environmentId,
                adapterId,
                manifest,
                package,
                cancellationToken) =>
            {
                using var bytes = new MemoryStream();
                await package.CopyToAsync(bytes, cancellationToken);
                published = new(
                    worldId,
                    stateId,
                    environmentId,
                    adapterId,
                    manifest,
                    bytes.ToArray());
                return Result(
                    RemotePrivateSnapshotUploadStatus.Published,
                    worldId,
                    stateId,
                    environmentId,
                    bytes.Length);
            });

        await publisher.PublishAllCurrentAsync();

        var actual = Assert.IsType<PublishedSnapshot>(published);
        Assert.Equal(fixture.World.Id, actual.WorldId);
        Assert.Equal(fixture.State.Id, actual.StateRevisionId);
        Assert.Equal(fixture.Environment.Id, actual.EnvironmentRevisionId);
        Assert.Equal(AdapterId, actual.GameAdapterId);
        Assert.Equal(
            fixture.Environment.Manifest.SchemaVersion,
            actual.EnvironmentManifest.SchemaVersion);
        Assert.Equal(
            fixture.Environment.Manifest.AdapterId,
            actual.EnvironmentManifest.AdapterId);
        Assert.Equal(
            fixture.Environment.Manifest.GameVersion,
            actual.EnvironmentManifest.GameVersion);
        Assert.Equal(
            fixture.Environment.Manifest.Components.ToArray(),
            actual.EnvironmentManifest.Components.ToArray());
        Assert.Equal(
            fixture.Environment.Manifest.Configuration.OrderBy(pair => pair.Key),
            actual.EnvironmentManifest.Configuration.OrderBy(pair => pair.Key));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, actual.Bytes);
    }

    [Fact]
    public async Task UnchangedConfirmedHeadDoesNotRehashOrReupload()
    {
        var storage = CreateStorage();
        await StoreCanonicalWorldAsync(storage, new byte[] { 8, 9 });
        var calls = 0;
        var publisher = new StewardOwnedWorldSnapshotPublisher(
            storage,
            (worldId, stateId, environmentId, _, _, package, _) =>
            {
                calls++;
                return Task.FromResult(Result(
                    RemotePrivateSnapshotUploadStatus.Published,
                    worldId,
                    stateId,
                    environmentId,
                    package.Length));
            });

        await publisher.PublishAllCurrentAsync();
        await publisher.PublishAllCurrentAsync();

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SharedWorldAndMissingPayloadDoNotUpload()
    {
        var storage = CreateStorage();
        await StoreCanonicalWorldAsync(
            storage,
            new byte[] { 1 },
            sharingMode: WorldSharingMode.Shared);
        var missing = await StoreCanonicalWorldAsync(storage, new byte[] { 2 });
        Assert.True(await storage.EvictRevisionPayloadAsync(
            missing.World.Id,
            missing.State.Id));
        var calls = 0;
        var publisher = new StewardOwnedWorldSnapshotPublisher(
            storage,
            (_, _, _, _, _, _, _) =>
            {
                calls++;
                throw new InvalidOperationException("Upload must not be called.");
            });

        await publisher.PublishAllCurrentAsync();

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task MalformedWorldDoesNotBlockIndependentValidSnapshot()
    {
        var storage = CreateStorage();
        var valid = await StoreCanonicalWorldAsync(storage, new byte[] { 3, 4 });
        var malformed = new World(
            WorldId.New(),
            "Malformed",
            AdapterId,
            Array.Empty<UserIdentity>(),
            CurrentEnvironmentRevisionId: null,
            CurrentStateRevisionId: RevisionId.New());
        await storage.SaveWorldAsync(malformed);
        var uploaded = new List<WorldId>();
        var publisher = new StewardOwnedWorldSnapshotPublisher(
            storage,
            (worldId, stateId, environmentId, _, _, package, _) =>
            {
                uploaded.Add(worldId);
                return Task.FromResult(Result(
                    RemotePrivateSnapshotUploadStatus.Published,
                    worldId,
                    stateId,
                    environmentId,
                    package.Length));
            });

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => publisher.PublishAllCurrentAsync());

        Assert.Contains(valid.World.Id, uploaded);
        Assert.Contains(
            "only one side of its canonical state/environment head",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypedFailureDoesNotBlockIndependentWorldAndFailsPass()
    {
        var storage = CreateStorage();
        var failed = await StoreCanonicalWorldAsync(storage, new byte[] { 5 });
        var successful = await StoreCanonicalWorldAsync(storage, new byte[] { 6 });
        var uploaded = new HashSet<WorldId>();
        var publisher = new StewardOwnedWorldSnapshotPublisher(
            storage,
            (worldId, stateId, environmentId, _, _, package, _) =>
            {
                uploaded.Add(worldId);
                var status = worldId == failed.World.Id
                    ? RemotePrivateSnapshotUploadStatus.PublicationBlocked
                    : RemotePrivateSnapshotUploadStatus.Published;
                return Task.FromResult(Result(
                    status,
                    worldId,
                    stateId,
                    environmentId,
                    package.Length));
            });

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => publisher.PublishAllCurrentAsync());

        Assert.Contains(failed.World.Id, uploaded);
        Assert.Contains(successful.World.Id, uploaded);
        Assert.Contains(
            "PublicationBlocked",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlreadyPublishedIsSuccessfulIdempotentCompletion()
    {
        var storage = CreateStorage();
        var fixture = await StoreCanonicalWorldAsync(storage, new byte[] { 7 });
        var publisher = new StewardOwnedWorldSnapshotPublisher(
            storage,
            (worldId, stateId, environmentId, _, _, package, _) =>
                Task.FromResult(Result(
                    RemotePrivateSnapshotUploadStatus.AlreadyPublished,
                    worldId,
                    stateId,
                    environmentId,
                    package.Length)));

        await publisher.PublishAllCurrentAsync();

        Assert.True(await storage.IsRevisionPayloadAvailableAsync(
            fixture.World.Id,
            fixture.State.Id));
    }

    private LocalWorldStorage CreateStorage()
        => new(Path.Combine(_root, "world-storage"));

    private static async Task<CanonicalWorldFixture> StoreCanonicalWorldAsync(
        LocalWorldStorage storage,
        byte[] payload,
        WorldSharingMode sharingMode = WorldSharingMode.LocalOnly)
    {
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();
        var manifest = new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId,
            GameVersion: "1.0.0",
            Components: Array.Empty<EnvironmentComponent>(),
            Configuration: new Dictionary<string, string>());
        var environment = new EnvironmentRevision(
            environmentId,
            worldId,
            ParentRevisionId: null,
            ObservedAt,
            CreatedBy: null,
            manifest);
        var state = new StateRevision(
            stateId,
            worldId,
            ParentRevisionId: null,
            ObservedAt,
            CreatedBy: null,
            AdapterId,
            StatePackageId: $"state:{stateId}",
            environmentId);
        var world = new World(
            worldId,
            "Canonical World",
            AdapterId,
            Array.Empty<UserIdentity>(),
            environmentId,
            stateId)
        {
            SharingMode = sharingMode
        };

        await storage.StoreEnvironmentRevisionAsync(environment);
        await using (var package = new MemoryStream(payload, writable: false))
        {
            await storage.StoreRevisionAsync(state, package);
        }

        await storage.SaveWorldAsync(world);
        return new(world, state, environment);
    }

    private static RemotePrivateSnapshotUploadResult Result(
        RemotePrivateSnapshotUploadStatus status,
        WorldId worldId,
        RevisionId stateId,
        RevisionId environmentId,
        long byteSize)
        => new(
            status,
            worldId,
            stateId,
            environmentId,
            byteSize,
            new string('A', 64));

    private sealed record CanonicalWorldFixture(
        World World,
        StateRevision State,
        EnvironmentRevision Environment);

    private sealed record PublishedSnapshot(
        WorldId WorldId,
        RevisionId StateRevisionId,
        RevisionId EnvironmentRevisionId,
        string GameAdapterId,
        EnvironmentManifest EnvironmentManifest,
        byte[] Bytes);

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
}
