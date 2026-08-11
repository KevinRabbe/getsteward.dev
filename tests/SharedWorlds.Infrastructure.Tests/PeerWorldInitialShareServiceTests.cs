using System.Text;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Infrastructure.Sessions;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerWorldInitialShareServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"steward-peer-share-{Guid.NewGuid():N}");

    [Fact]
    public async Task FreshShareWritesActiveFenceBeforePublishingPeerWorld()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Events.Clear();
        var service = new PeerWorldInitialShareService(
            fixture.Storage,
            fixture.Fences);

        var shared = await service.ShareAsync(
            fixture.World.Id,
            fixture.Owner);

        Assert.Equal(new[] { "fence", "world" }, fixture.Events);
        Assert.Equal(WorldSharingMode.Shared, shared.SharingMode);
        Assert.NotNull(shared.PeerAuthority);
        Assert.Equal((ulong)1, shared.PeerAuthority.Generation);
        Assert.Equal(fixture.Owner.ExternalId, shared.PeerAuthority.Holder.ExternalId);
        Assert.Single(shared.Members);
        Assert.Equal(fixture.Owner.Provider, shared.Members[0].Provider);
        Assert.Equal(fixture.Owner.ExternalId, shared.Members[0].ExternalId);
        var fence = await fixture.Fences.LoadAsync(shared.Id);
        Assert.NotNull(fence);
        Assert.Equal(PeerAuthorityFenceState.Active, fence.State);
        Assert.Equal(shared.CurrentStateRevisionId, fence.StateRevisionId);
    }

    [Fact]
    public async Task FreshShareReplacesDeviceLocalMembershipWithCurrentPeerIdentity()
    {
        var fixture = await CreateFixtureAsync();
        var localOnlyIdentity = new UserIdentity("local", "this-pc", "This PC");
        var historical = fixture.World with
        {
            Members = [localOnlyIdentity]
        };
        await fixture.Storage.SaveWorldAsync(historical);
        fixture.Events.Clear();
        var service = new PeerWorldInitialShareService(
            fixture.Storage,
            fixture.Fences);

        var shared = await service.ShareAsync(
            fixture.World.Id,
            fixture.Owner);

        Assert.Single(shared.Members);
        Assert.Equal(fixture.Owner.Provider, shared.Members[0].Provider);
        Assert.Equal(fixture.Owner.ExternalId, shared.Members[0].ExternalId);
        Assert.DoesNotContain(
            shared.Members,
            member => string.Equals(member.Provider, "local", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "fence", "world" }, fixture.Events);
    }

    [Fact]
    public async Task FailedWorldPublicationLeavesSafeFenceAndExactRetryCompletes()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Events.Clear();
        fixture.Storage.FailWorldSave = true;
        var service = new PeerWorldInitialShareService(
            fixture.Storage,
            fixture.Fences);

        await Assert.ThrowsAsync<IOException>(() =>
            service.ShareAsync(fixture.World.Id, fixture.Owner));

        var afterFailure = await fixture.Storage.LoadWorldAsync(fixture.World.Id);
        Assert.NotNull(afterFailure);
        Assert.Equal(WorldSharingMode.LocalOnly, afterFailure.SharingMode);
        Assert.Null(afterFailure.PeerAuthority);
        var fence = await fixture.Fences.LoadAsync(fixture.World.Id);
        Assert.NotNull(fence);
        Assert.Equal(PeerAuthorityFenceState.Active, fence.State);
        Assert.Equal((ulong)1, fence.Generation);
        Assert.Equal(new[] { "fence", "world" }, fixture.Events);

        fixture.Storage.FailWorldSave = false;
        fixture.Events.Clear();
        var retried = await service.ShareAsync(fixture.World.Id, fixture.Owner);

        Assert.Equal(new[] { "world" }, fixture.Events);
        Assert.Equal(WorldSharingMode.Shared, retried.SharingMode);
        Assert.Equal((ulong)1, retried.PeerAuthority!.Generation);
    }

    [Fact]
    public async Task MissingCanonicalPayloadFailsBeforeAnyFenceOrWorldMutation()
    {
        var fixture = await CreateFixtureAsync();
        _ = await fixture.Storage.EvictRevisionPayloadAsync(
            fixture.World.Id,
            fixture.World.CurrentStateRevisionId!.Value);
        fixture.Events.Clear();
        var service = new PeerWorldInitialShareService(
            fixture.Storage,
            fixture.Fences);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ShareAsync(fixture.World.Id, fixture.Owner));

        Assert.Empty(fixture.Events);
        Assert.Null(await fixture.Fences.LoadAsync(fixture.World.Id));
        var world = await fixture.Storage.LoadWorldAsync(fixture.World.Id);
        Assert.Equal(WorldSharingMode.LocalOnly, world!.SharingMode);
    }

    [Fact]
    public async Task ConflictingExistingFenceFailsClosedBeforeWorldPublication()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Fences.Record = new PeerAuthorityFence(
            fixture.World.Id,
            new UserIdentity("steam", "9999", "Other"),
            1,
            fixture.World.CurrentStateRevisionId!.Value,
            PeerAuthorityFenceState.Active,
            DateTimeOffset.UtcNow);
        fixture.Events.Clear();
        var service = new PeerWorldInitialShareService(
            fixture.Storage,
            fixture.Fences);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ShareAsync(fixture.World.Id, fixture.Owner));

        Assert.Empty(fixture.Events);
        var world = await fixture.Storage.LoadWorldAsync(fixture.World.Id);
        Assert.Equal(WorldSharingMode.LocalOnly, world!.SharingMode);
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        Directory.CreateDirectory(_root);
        var inner = new LocalWorldStorage(
            Path.Combine(_root, Guid.NewGuid().ToString("N")));
        var events = new List<string>();
        var storage = new RecordingStorage(inner, events);
        var fences = new RecordingFenceStore(events);
        var owner = new UserIdentity("steam", "1001", "Owner");
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();
        var manifest = new EnvironmentManifest(
            1,
            "fake",
            "1.0.0",
            [],
            new Dictionary<string, string>());
        var environment = new EnvironmentRevision(
            environmentId,
            worldId,
            null,
            DateTimeOffset.UtcNow,
            owner,
            manifest);
        var state = new StateRevision(
            stateId,
            worldId,
            null,
            DateTimeOffset.UtcNow,
            owner,
            "fake",
            "package",
            environmentId);
        var world = new World(
            worldId,
            "Fresh Share",
            "fake",
            [owner],
            environmentId,
            stateId)
        {
            SharingMode = WorldSharingMode.LocalOnly
        };

        await storage.StoreEnvironmentRevisionAsync(environment);
        await using (var payload = new MemoryStream(
                         Encoding.UTF8.GetBytes("canonical-state"),
                         writable: false))
        {
            await storage.StoreRevisionAsync(state, payload);
        }
        await storage.SaveWorldAsync(world);

        return new Fixture(storage, fences, events, world, owner);
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Fixture(
        RecordingStorage Storage,
        RecordingFenceStore Fences,
        List<string> Events,
        World World,
        UserIdentity Owner);

    private sealed class RecordingFenceStore : IPeerAuthorityFenceStore
    {
        private readonly List<string> _events;

        public RecordingFenceStore(List<string> events)
            => _events = events;

        public PeerAuthorityFence? Record { get; set; }

        public Task<PeerAuthorityFence?> LoadAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Record?.WorldId == worldId ? Record : null);
        }

        public Task SaveAsync(
            PeerAuthorityFence fence,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add("fence");
            Record = fence;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStorage : IWorldStorage
    {
        private readonly LocalWorldStorage _inner;
        private readonly List<string> _events;

        public RecordingStorage(LocalWorldStorage inner, List<string> events)
        {
            _inner = inner;
            _events = events;
        }

        public bool FailWorldSave { get; set; }

        public Task SaveWorldAsync(
            World world,
            CancellationToken cancellationToken = default)
        {
            _events.Add("world");
            return FailWorldSave
                ? Task.FromException(new IOException("Synthetic World publication failure."))
                : _inner.SaveWorldAsync(world, cancellationToken);
        }

        public Task<World?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => _inner.LoadWorldAsync(worldId, cancellationToken);

        public Task<IReadOnlyList<World>> ListWorldsAsync(
            CancellationToken cancellationToken = default)
            => _inner.ListWorldsAsync(cancellationToken);

        public Task<bool> DeleteWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => _inner.DeleteWorldAsync(worldId, cancellationToken);

        public Task StoreEnvironmentRevisionAsync(
            EnvironmentRevision revision,
            CancellationToken cancellationToken = default)
            => _inner.StoreEnvironmentRevisionAsync(revision, cancellationToken);

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => _inner.LoadEnvironmentRevisionAsync(worldId, revisionId, cancellationToken);

        public Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
            => _inner.StoreRevisionAsync(revision, package, cancellationToken);

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
            => _inner.IsRevisionPayloadAvailableAsync(worldId, revisionId, cancellationToken);

        public Task<long?> GetRevisionPayloadSizeAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => _inner.GetRevisionPayloadSizeAsync(worldId, revisionId, cancellationToken);

        public Task<bool> EvictRevisionPayloadAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => _inner.EvictRevisionPayloadAsync(worldId, revisionId, cancellationToken);
    }
}
