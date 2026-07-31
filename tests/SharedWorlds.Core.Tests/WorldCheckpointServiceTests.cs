using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorldCheckpointServiceTests
{
    [Fact]
    public async Task SetAddsHumanLabelWithoutChangingWorldHeadsOrStateBytes()
    {
        var fixture = CreateFixture();
        var service = new WorldCheckpointService(fixture.Storage);

        var updated = await service.SetAsync(
            fixture.World,
            fixture.Initial.Id,
            "Before the boss",
            fixture.Owner);

        var checkpoint = Assert.Single(updated.Checkpoints);
        Assert.Equal(fixture.Initial.Id, checkpoint.StateRevisionId);
        Assert.Equal("Before the boss", checkpoint.Name);
        Assert.Equal(fixture.Owner, checkpoint.CreatedBy);
        Assert.Equal(fixture.World.CurrentStateRevisionId, updated.CurrentStateRevisionId);
        Assert.Equal(fixture.World.CurrentEnvironmentRevisionId, updated.CurrentEnvironmentRevisionId);
        Assert.Equal(fixture.PayloadsBefore, fixture.Storage.Payloads);
        Assert.Equal(updated, await fixture.Storage.LoadWorldAsync(updated.Id));
    }

    [Fact]
    public async Task SetRenamesExistingRevisionWithoutCreatingSecondCheckpoint()
    {
        var fixture = CreateFixture();
        var service = new WorldCheckpointService(fixture.Storage);
        var named = await service.SetAsync(
            fixture.World,
            fixture.Current.Id,
            "First name",
            fixture.Owner);

        var renamed = await service.SetAsync(
            named,
            fixture.Current.Id,
            "Final name",
            fixture.Owner);

        var checkpoint = Assert.Single(renamed.Checkpoints);
        Assert.Equal(fixture.Current.Id, checkpoint.StateRevisionId);
        Assert.Equal("Final name", checkpoint.Name);
    }

    [Fact]
    public async Task DuplicateNameOnDifferentRevisionIsRejectedCaseInsensitively()
    {
        var fixture = CreateFixture();
        var service = new WorldCheckpointService(fixture.Storage);
        var named = await service.SetAsync(
            fixture.World,
            fixture.Initial.Id,
            "Launch Day",
            fixture.Owner);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetAsync(
                named,
                fixture.Current.Id,
                "launch day",
                fixture.Owner));

        Assert.Contains("already has a checkpoint", exception.Message, StringComparison.Ordinal);
        Assert.Single(named.Checkpoints);
    }

    [Fact]
    public async Task RevisionOutsideVisibleCanonicalHistoryCannotBeNamed()
    {
        var fixture = CreateFixture();
        var service = new WorldCheckpointService(fixture.Storage);
        var orphan = new StateRevision(
            RevisionId.New(),
            fixture.World.Id,
            ParentRevisionId: null,
            DateTimeOffset.UtcNow,
            fixture.Owner,
            fixture.World.GameAdapterId,
            "orphan",
            EnvironmentRevisionId: fixture.World.CurrentEnvironmentRevisionId);
        await fixture.Storage.StoreRevisionAsync(
            orphan,
            new MemoryStream([9], writable: false));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetAsync(fixture.World, orphan.Id, "Orphan", fixture.Owner));

        Assert.Contains("visible canonical World History", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveDeletesOnlyLabel()
    {
        var fixture = CreateFixture();
        var service = new WorldCheckpointService(fixture.Storage);
        var named = await service.SetAsync(
            fixture.World,
            fixture.Initial.Id,
            "Keep this",
            fixture.Owner);

        var updated = await service.RemoveAsync(named, fixture.Initial.Id);

        Assert.Empty(updated.Checkpoints);
        Assert.NotNull(await fixture.Storage.LoadStateRevisionAsync(
            fixture.World.Id,
            fixture.Initial.Id));
        Assert.Equal(fixture.PayloadsBefore, fixture.Storage.Payloads);
        Assert.Equal(fixture.World.CurrentStateRevisionId, updated.CurrentStateRevisionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line\nbreak")]
    public async Task InvalidNamesAreRejected(string name)
    {
        var fixture = CreateFixture();
        var service = new WorldCheckpointService(fixture.Storage);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            service.SetAsync(fixture.World, fixture.Current.Id, name, fixture.Owner));
    }

    [Fact]
    public async Task CheckpointCountIsBounded()
    {
        var owner = new UserIdentity("local", "owner", "Owner");
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var storage = new MemoryWorldStorage();
        RevisionId? parent = null;
        var revisions = new List<StateRevision>();
        for (var index = 0; index <= WorldCheckpointService.MaximumCheckpointsPerWorld; index++)
        {
            var revision = new StateRevision(
                RevisionId.New(),
                worldId,
                parent,
                DateTimeOffset.UtcNow.AddMinutes(index),
                owner,
                "test-adapter",
                $"package-{index}",
                EnvironmentRevisionId: environmentId);
            await storage.StoreRevisionAsync(
                revision,
                new MemoryStream([(byte)index], writable: false));
            revisions.Add(revision);
            parent = revision.Id;
        }

        var world = new World(
            worldId,
            "Bounded",
            "test-adapter",
            [owner],
            environmentId,
            revisions[^1].Id);
        await storage.SaveWorldAsync(world);
        var service = new WorldCheckpointService(storage);
        var updated = world;
        for (var index = 0; index < WorldCheckpointService.MaximumCheckpointsPerWorld; index++)
        {
            updated = await service.SetAsync(
                updated,
                revisions[^(index + 1)].Id,
                $"Checkpoint {index + 1}",
                owner);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetAsync(
                updated,
                revisions[0].Id,
                "Too many",
                owner));

        Assert.Contains("at most 64", exception.Message, StringComparison.Ordinal);
        Assert.Equal(WorldCheckpointService.MaximumCheckpointsPerWorld, updated.Checkpoints.Count);
    }

    private static Fixture CreateFixture()
    {
        var owner = new UserIdentity("local", "owner", "Owner");
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var initial = new StateRevision(
            RevisionId.New(),
            worldId,
            ParentRevisionId: null,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            owner,
            "test-adapter",
            "initial",
            EnvironmentRevisionId: environmentId);
        var current = new StateRevision(
            RevisionId.New(),
            worldId,
            initial.Id,
            DateTimeOffset.UtcNow,
            owner,
            "test-adapter",
            "current",
            EnvironmentRevisionId: environmentId);
        var world = new World(
            worldId,
            "Checkpoint World",
            "test-adapter",
            [owner],
            environmentId,
            current.Id);
        var storage = new MemoryWorldStorage();
        storage.StoreRevisionAsync(initial, new MemoryStream([1, 2], writable: false))
            .GetAwaiter().GetResult();
        storage.StoreRevisionAsync(current, new MemoryStream([3, 4], writable: false))
            .GetAwaiter().GetResult();
        storage.SaveWorldAsync(world).GetAwaiter().GetResult();
        return new Fixture(
            storage,
            world,
            initial,
            current,
            owner,
            storage.Payloads.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
    }

    private sealed record Fixture(
        MemoryWorldStorage Storage,
        World World,
        StateRevision Initial,
        StateRevision Current,
        UserIdentity Owner,
        Dictionary<RevisionId, byte[]> PayloadsBefore);

    private sealed class MemoryWorldStorage : IWorldStorage
    {
        private readonly Dictionary<WorldId, World> _worlds = [];
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
            => Task.CompletedTask;

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<EnvironmentRevision?>(null);

        public async Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
        {
            _revisions[(revision.WorldId, revision.Id)] = revision;
            await using var target = new MemoryStream();
            await package.CopyToAsync(target, cancellationToken);
            Payloads[revision.Id] = target.ToArray();
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
            => Task.FromResult<Stream>(new MemoryStream(Payloads[revisionId], writable: false));
    }
}
