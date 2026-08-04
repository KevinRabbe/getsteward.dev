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
    public async Task PublishesExactBytesBeforeExactRevisionEvidence()
    {
        var storage = CreateStorage();
        var fixture = await StoreCanonicalWorldAsync(
            storage,
            new byte[] { 1, 2, 3, 4 });
        var order = new List<string>();
        byte[]? publishedBytes = null;
        StateRevision? publishedState = null;
        EnvironmentRevision? publishedEnvironment = null;
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
                order.Add("bytes");
                Assert.Equal(fixture.World.Id, worldId);
                Assert.Equal(fixture.State.Id, stateId);
                Assert.Equal(fixture.Environment.Id, environmentId);
                Assert.Equal(AdapterId, adapterId);
                Assert.Equal(AdapterId, manifest.AdapterId);
                using var bytes = new MemoryStream();
                await package.CopyToAsync(bytes, cancellationToken);
                publishedBytes = bytes.ToArray();
                return UploadResult(
                    RemotePrivateSnapshotUploadStatus.Published,
                    fixture,
                    bytes.Length);
            },
            (worldId, state, environment, _) =>
            {
                order.Add("evidence");
                Assert.Equal(fixture.World.Id, worldId);
                publishedState = state;
                publishedEnvironment = environment;
                return Task.FromResult(EvidenceResult(
                    RemotePrivateSnapshotRevisionEvidenceStatus.Published,
                    fixture));
            });

        await publisher.PublishAllCurrentAsync();

        Assert.Equal(new[] { "bytes", "evidence" }, order);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, publishedBytes);
        Assert.Equal(fixture.State.Id, publishedState?.Id);
        Assert.Equal(fixture.State.ParentRevisionId, publishedState?.ParentRevisionId);
        Assert.Equal(fixture.State.StatePackageId, publishedState?.StatePackageId);
        Assert.Equal(fixture.Environment.Id, publishedEnvironment?.Id);
        Assert.Equal(
            fixture.Environment.ParentRevisionId,
            publishedEnvironment?.ParentRevisionId);
        Assert.Equal(
            fixture.Environment.Manifest.AdapterId,
            publishedEnvironment?.Manifest.AdapterId);
    }

    [Fact]
    public async Task InterruptedEvidenceRepairDoesNotReopenOrReuploadBytes()
    {
        var storage = CreateStorage();
        var fixture = await StoreCanonicalWorldAsync(storage, new byte[] { 5, 6 });
        var byteCalls = 0;
        var evidenceCalls = 0;
        var publisher = new StewardOwnedWorldSnapshotPublisher(
            storage,
            (worldId, stateId, environmentId, _, _, package, _) =>
            {
                byteCalls++;
                return Task.FromResult(UploadResult(
                    RemotePrivateSnapshotUploadStatus.Published,
                    fixture,
                    package.Length));
            },
            (_, _, _, _) =>
            {
                evidenceCalls++;
                var status = evidenceCalls == 1
                    ? RemotePrivateSnapshotRevisionEvidenceStatus.Conflict
                    : RemotePrivateSnapshotRevisionEvidenceStatus.AlreadyPublished;
                return Task.FromResult(EvidenceResult(status, fixture));
            });

        await Assert.ThrowsAsync<AggregateException>(
            () => publisher.PublishAllCurrentAsync());
        await publisher.PublishAllCurrentAsync();

        Assert.Equal(1, byteCalls);
        Assert.Equal(2, evidenceCalls);
    }

    [Fact]
    public async Task FullyConfirmedUnchangedHeadSkipsAllFurtherWork()
    {
        var storage = CreateStorage();
        var fixture = await StoreCanonicalWorldAsync(storage, new byte[] { 7, 8 });
        var byteCalls = 0;
        var evidenceCalls = 0;
        var publisher = new StewardOwnedWorldSnapshotPublisher(
            storage,
            (_, _, _, _, _, package, _) =>
            {
                byteCalls++;
                return Task.FromResult(UploadResult(
                    RemotePrivateSnapshotUploadStatus.AlreadyPublished,
                    fixture,
                    package.Length));
            },
            (_, _, _, _) =>
            {
                evidenceCalls++;
                return Task.FromResult(EvidenceResult(
                    RemotePrivateSnapshotRevisionEvidenceStatus.AlreadyPublished,
                    fixture));
            });

        await publisher.PublishAllCurrentAsync();
        await publisher.PublishAllCurrentAsync();

        Assert.Equal(1, byteCalls);
        Assert.Equal(1, evidenceCalls);
    }

    [Fact]
    public async Task SharedWorldAndMissingPayloadPublishNothing()
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
        var byteCalls = 0;
        var evidenceCalls = 0;
        var publisher = new StewardOwnedWorldSnapshotPublisher(
            storage,
            (_, _, _, _, _, _, _) =>
            {
                byteCalls++;
                throw new InvalidOperationException("Bytes must not publish.");
            },
            (_, _, _, _) =>
            {
                evidenceCalls++;
                throw new InvalidOperationException("Evidence must not publish.");
            });

        await publisher.PublishAllCurrentAsync();

        Assert.Equal(0, byteCalls);
        Assert.Equal(0, evidenceCalls);
    }

    [Fact]
    public async Task MalformedWorldDoesNotBlockIndependentCompletePublication()
    {
        var storage = CreateStorage();
        var valid = await StoreCanonicalWorldAsync(storage, new byte[] { 9 });
        var malformed = new World(
            WorldId.New(),
            "Malformed",
            AdapterId,
            Array.Empty<UserIdentity>(),
            CurrentEnvironmentRevisionId: null,
            CurrentStateRevisionId: RevisionId.New());
        await storage.SaveWorldAsync(malformed);
        var completed = new List<WorldId>();
        var publisher = new StewardOwnedWorldSnapshotPublisher(
            storage,
            (worldId, _, _, _, _, package, _) =>
                Task.FromResult(UploadResult(
                    RemotePrivateSnapshotUploadStatus.Published,
                    valid,
                    package.Length)),
            (worldId, _, _, _) =>
            {
                completed.Add(worldId);
                return Task.FromResult(EvidenceResult(
                    RemotePrivateSnapshotRevisionEvidenceStatus.Published,
                    valid));
            });

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => publisher.PublishAllCurrentAsync());

        Assert.Contains(valid.World.Id, completed);
        Assert.Contains(
            "only one side of its canonical state/environment head",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ByteFailureNeverPublishesEvidenceAndIndependentWorldContinues()
    {
        var storage = CreateStorage();
        var failed = await StoreCanonicalWorldAsync(storage, new byte[] { 10 });
        var successful = await StoreCanonicalWorldAsync(storage, new byte[] { 11 });
        var evidenceWorlds = new List<WorldId>();
        var publisher = new StewardOwnedWorldSnapshotPublisher(
            storage,
            (worldId, stateId, environmentId, _, _, package, _) =>
            {
                var fixture = worldId == failed.World.Id ? failed : successful;
                var status = worldId == failed.World.Id
                    ? RemotePrivateSnapshotUploadStatus.PublicationBlocked
                    : RemotePrivateSnapshotUploadStatus.Published;
                return Task.FromResult(UploadResult(
                    status,
                    fixture,
                    package.Length));
            },
            (worldId, _, _, _) =>
            {
                evidenceWorlds.Add(worldId);
                return Task.FromResult(EvidenceResult(
                    RemotePrivateSnapshotRevisionEvidenceStatus.Published,
                    successful));
            });

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => publisher.PublishAllCurrentAsync());

        Assert.DoesNotContain(failed.World.Id, evidenceWorlds);
        Assert.Contains(successful.World.Id, evidenceWorlds);
        Assert.Contains(
            "PublicationBlocked",
            exception.ToString(),
            StringComparison.Ordinal);
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
            ParentRevisionId: RevisionId.New(),
            ObservedAt.AddMinutes(-1),
            CreatedBy: null,
            manifest);
        var state = new StateRevision(
            stateId,
            worldId,
            ParentRevisionId: RevisionId.New(),
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

    private static RemotePrivateSnapshotUploadResult UploadResult(
        RemotePrivateSnapshotUploadStatus status,
        CanonicalWorldFixture fixture,
        long byteSize)
        => new(
            status,
            fixture.World.Id,
            fixture.State.Id,
            fixture.Environment.Id,
            byteSize,
            new string('A', 64));

    private static RemotePrivateSnapshotRevisionEvidenceResult EvidenceResult(
        RemotePrivateSnapshotRevisionEvidenceStatus status,
        CanonicalWorldFixture fixture)
        => new(
            status,
            fixture.World.Id,
            fixture.State.Id,
            fixture.Environment.Id,
            status is RemotePrivateSnapshotRevisionEvidenceStatus.Published or
                RemotePrivateSnapshotRevisionEvidenceStatus.AlreadyPublished
                ? ObservedAt.AddMinutes(1)
                : null);

    private sealed record CanonicalWorldFixture(
        World World,
        StateRevision State,
        EnvironmentRevision Environment);

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
