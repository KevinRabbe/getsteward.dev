using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorldHistoryServiceTests
{
    [Fact]
    public async Task HistoryFollowsCanonicalParentChainNewestFirst()
    {
        var fixture = CreateFixture();
        var service = new WorldHistoryService(fixture.Storage);

        var history = await service.GetHistoryAsync(fixture.World);

        Assert.False(history.HasOlderRevisions);
        Assert.Equal(
            new[] { fixture.Current.Id, fixture.Middle.Id, fixture.Initial.Id },
            history.Revisions.Select(revision => revision.Id));
    }

    [Fact]
    public async Task RestoreCreatesNewHeadWithoutDeletingLaterHistory()
    {
        var fixture = CreateFixture();
        var service = new WorldHistoryService(fixture.Storage);
        var actor = new UserIdentity("local", "restorer", "Restorer");

        var restored = await service.RestoreAsync(
            fixture.World,
            fixture.Initial.Id,
            actor);

        Assert.NotEqual(fixture.Current.Id, restored.CurrentStateRevisionId);
        var restoredRevision = await fixture.Storage.LoadStateRevisionAsync(
            fixture.World.Id,
            restored.CurrentStateRevisionId!.Value);
        Assert.NotNull(restoredRevision);
        Assert.Equal(fixture.Current.Id, restoredRevision.ParentRevisionId);
        Assert.Equal(actor, restoredRevision.CreatedBy);
        Assert.Equal(fixture.Initial.StatePackageId, restoredRevision.StatePackageId);
        Assert.Equal(
            fixture.Storage.Payloads[fixture.Initial.Id],
            fixture.Storage.Payloads[restoredRevision.Id]);

        Assert.NotNull(await fixture.Storage.LoadStateRevisionAsync(fixture.World.Id, fixture.Middle.Id));
        Assert.NotNull(await fixture.Storage.LoadStateRevisionAsync(fixture.World.Id, fixture.Current.Id));

        var history = await service.GetHistoryAsync(restored);
        Assert.Equal(restoredRevision.Id, history.Revisions[0].Id);
        Assert.Equal(fixture.Current.Id, history.Revisions[1].Id);
    }

    [Fact]
    public async Task MakeMyCopyCreatesIndependentLocalWorldFromHistoricalState()
    {
        var fixture = CreateFixture();
        var service = new WorldHistoryService(fixture.Storage);
        var owner = new UserIdentity("local", "viewer", "Viewer");

        var copy = await service.MakeIndependentCopyAsync(
            fixture.World,
            fixture.Middle.Id,
            "History Copy",
            owner);

        Assert.NotEqual(fixture.World.Id, copy.Id);
        Assert.Equal("History Copy", copy.Name);
        Assert.Equal(WorldSharingMode.LocalOnly, copy.SharingMode);
        Assert.Equal(WorldVisibility.Private, copy.Visibility);
        Assert.Equal(owner, Assert.Single(copy.Members));
        Assert.Equal(fixture.World.Name, copy.StartedFrom?.WorldName);
        Assert.Equal(fixture.Middle.Id.ToString(), copy.StartedFrom?.SnapshotId);

        var copiedState = await fixture.Storage.LoadStateRevisionAsync(
            copy.Id,
            copy.CurrentStateRevisionId!.Value);
        Assert.NotNull(copiedState);
        Assert.Null(copiedState.ParentRevisionId);
        Assert.Equal(
            fixture.Storage.Payloads[fixture.Middle.Id],
            fixture.Storage.Payloads[copiedState.Id]);

        var copiedEnvironment = await fixture.Storage.LoadEnvironmentRevisionAsync(
            copy.Id,
            copy.CurrentEnvironmentRevisionId!.Value);
        Assert.NotNull(copiedEnvironment);
        Assert.Equal(fixture.Environment.Manifest, copiedEnvironment.Manifest);
        Assert.Null(copiedEnvironment.ParentRevisionId);
    }

    [Fact]
    public async Task RestoreRejectsRevisionOutsideBoundedCanonicalHistory()
    {
        var fixture = CreateFixture();
        var orphan = new StateRevision(
            RevisionId.New(),
            fixture.World.Id,
            ParentRevisionId: null,
            DateTimeOffset.UtcNow,
            fixture.Owner,
            fixture.World.GameAdapterId,
            "orphan-package");
        await fixture.Storage.StoreRevisionAsync(
            orphan,
            new MemoryStream([9, 9, 9], writable: false));

        var service = new WorldHistoryService(fixture.Storage);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreAsync(fixture.World, orphan.Id, fixture.Owner));

        Assert.Contains("not in the bounded current World History", exception.Message, StringComparison.Ordinal);
        Assert.Equal(fixture.Current.Id, fixture.World.CurrentStateRevisionId);
    }

    [Fact]
    public async Task MutationRefusesToGuessEnvironmentAfterEnvironmentHistoryChanged()
    {
        var fixture = CreateFixture();
        var changedEnvironment = new EnvironmentRevision(
            RevisionId.New(),
            fixture.World.Id,
            fixture.Environment.Id,
            DateTimeOffset.UtcNow,
            fixture.Owner,
            fixture.Environment.Manifest with { GameVersion = "2.0" });
        await fixture.Storage.StoreEnvironmentRevisionAsync(changedEnvironment);
        var changedWorld = fixture.World with
        {
            CurrentEnvironmentRevisionId = changedEnvironment.Id
        };
        await fixture.Storage.SaveWorldAsync(changedWorld);
        var service = new WorldHistoryService(fixture.Storage);

        var restore = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RestoreAsync(changedWorld, fixture.Initial.Id, fixture.Owner));
        var copy = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MakeIndependentCopyAsync(
                changedWorld,
                fixture.Initial.Id,
                "Unsafe Copy",
                fixture.Owner));

        Assert.Contains("environment changed", restore.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("environment changed", copy.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fixture.Current.Id, changedWorld.CurrentStateRevisionId);
        Assert.Null(await fixture.Storage.LoadWorldAsync(WorldId.New()));
    }

    [Fact]
    public async Task HistoryFailsClosedOnParentCycle()
    {
        var owner = new UserIdentity("local", "owner", "Owner");
        var worldId = WorldId.New();
        var firstId = RevisionId.New();
        var secondId = RevisionId.New();
        var first = new StateRevision(
            firstId,
            worldId,
            secondId,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            owner,
            "test-adapter",
            "first");
        var second = new StateRevision(
            secondId,
            worldId,
            firstId,
            DateTimeOffset.UtcNow,
            owner,
            "test-adapter",
            "second");
        var storage = new MemoryWorldStorage();
        await storage.StoreRevisionAsync(first, new MemoryStream([1], writable: false));
        await storage.StoreRevisionAsync(second, new MemoryStream([2], writable: false));
        var world = new World(
            worldId,
            "Cyclic",
            "test-adapter",
            [owner],
            CurrentEnvironmentRevisionId: null,
            CurrentStateRevisionId: secondId);

        var service = new WorldHistoryService(storage);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.GetHistoryAsync(world));

        Assert.Contains("cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static Fixture CreateFixture()
    {
        var owner = new UserIdentity("local", "owner", "Owner");
        var worldId = WorldId.New();
        var environment = new EnvironmentRevision(
            RevisionId.New(),
            worldId,
            ParentRevisionId: null,
            DateTimeOffset.UtcNow.AddHours(-1),
            owner,
            new EnvironmentManifest(
                SchemaVersion: 1,
                AdapterId: "test-adapter",
                GameVersion: "1.0",
                Components: [],
                Configuration: new Dictionary<string, string>()));
        var initial = new StateRevision(
            RevisionId.New(),
            worldId,
            ParentRevisionId: null,
            DateTimeOffset.UtcNow.AddMinutes(-30),
            owner,
            "test-adapter",
            "initial-package");
        var middle = new StateRevision(
            RevisionId.New(),
            worldId,
            initial.Id,
            DateTimeOffset.UtcNow.AddMinutes(-20),
            owner,
            "test-adapter",
            "middle-package");
        var current = new StateRevision(
            RevisionId.New(),
            worldId,
            middle.Id,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            owner,
            "test-adapter",
            "current-package");
        var world = new World(
            worldId,
            "Source World",
            "test-adapter",
            [owner],
            environment.Id,
            current.Id);

        var storage = new MemoryWorldStorage();
        storage.StoreEnvironmentRevisionAsync(environment).GetAwaiter().GetResult();
        storage.StoreRevisionAsync(initial, new MemoryStream([1, 2, 3], writable: false)).GetAwaiter().GetResult();
        storage.StoreRevisionAsync(middle, new MemoryStream([4, 5, 6], writable: false)).GetAwaiter().GetResult();
        storage.StoreRevisionAsync(current, new MemoryStream([7, 8, 9], writable: false)).GetAwaiter().GetResult();
        storage.SaveWorldAsync(world).GetAwaiter().GetResult();
        return new Fixture(storage, world, environment, initial, middle, current, owner);
    }

    private sealed record Fixture(
        MemoryWorldStorage Storage,
        World World,
        EnvironmentRevision Environment,
        StateRevision Initial,
        StateRevision Middle,
        StateRevision Current,
        UserIdentity Owner);

    private sealed class MemoryWorldStorage : IWorldStorage
    {
        private readonly Dictionary<WorldId, World> _worlds = [];
        private readonly Dictionary<(WorldId WorldId, RevisionId RevisionId), EnvironmentRevision> _environments = [];
        private readonly Dictionary<(WorldId WorldId, RevisionId RevisionId), StateRevision> _revisions = [];

        public Dictionary<RevisionId, byte[]> Payloads { get; } = [];

        public Task SaveWorldAsync(World world, CancellationToken cancellationToken = default)
        {
            _worlds[world.Id] = world;
            return Task.CompletedTask;
        }

        public Task<World?> LoadWorldAsync(WorldId worldId, CancellationToken cancellationToken = default)
            => Task.FromResult(_worlds.GetValueOrDefault(worldId));

        public Task<IReadOnlyList<World>> ListWorldsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<World>>(_worlds.Values.ToArray());

        public Task StoreEnvironmentRevisionAsync(
            EnvironmentRevision revision,
            CancellationToken cancellationToken = default)
        {
            _environments[(revision.WorldId, revision.Id)] = revision;
            return Task.CompletedTask;
        }

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_environments.GetValueOrDefault((worldId, revisionId)));

        public async Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
        {
            _revisions[(revision.WorldId, revision.Id)] = revision;
            await using var copy = new MemoryStream();
            await package.CopyToAsync(copy, cancellationToken);
            Payloads[revision.Id] = copy.ToArray();
        }

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_revisions.GetValueOrDefault((worldId, revisionId)));

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            if (!_revisions.ContainsKey((worldId, revisionId)) ||
                !Payloads.TryGetValue(revisionId, out var payload))
            {
                throw new FileNotFoundException();
            }

            return Task.FromResult<Stream>(new MemoryStream(payload, writable: false));
        }
    }
}
