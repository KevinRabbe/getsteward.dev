using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorldSettingsServiceTests
{
    [Fact]
    public void World_DefaultsToKeepExactGameVersion()
    {
        var world = CreateWorld();

        Assert.Equal(WorldGameVersionPolicy.KeepExact, world.GameVersionPolicy);
    }

    [Fact]
    public async Task SetGameVersionPolicy_PersistsExplicitChange()
    {
        var world = CreateWorld();
        var storage = new RecordingWorldStorage(world);
        var settings = new WorldSettingsService(storage);

        var updated = await settings.SetGameVersionPolicyAsync(
            world.Id,
            WorldGameVersionPolicy.AllowUpdateCandidates);

        Assert.Equal(WorldGameVersionPolicy.AllowUpdateCandidates, updated.GameVersionPolicy);
        Assert.Equal(
            WorldGameVersionPolicy.AllowUpdateCandidates,
            storage.Worlds[world.Id].GameVersionPolicy);
        Assert.Equal(world.CurrentEnvironmentRevisionId, updated.CurrentEnvironmentRevisionId);
        Assert.Equal(world.CurrentStateRevisionId, updated.CurrentStateRevisionId);
    }

    [Fact]
    public async Task SetGameVersionPolicy_RejectsUnknownWorld()
    {
        var settings = new WorldSettingsService(new RecordingWorldStorage());
        var worldId = WorldId.New();

        var exception = await Assert.ThrowsAsync<WorldNotFoundException>(() =>
            settings.SetGameVersionPolicyAsync(
                worldId,
                WorldGameVersionPolicy.AllowUpdateCandidates));

        Assert.Equal(worldId, exception.WorldId);
    }

    private static World CreateWorld()
        => new(
            Id: WorldId.New(),
            Name: "Versioned World",
            GameAdapterId: "test-adapter",
            Members: [new UserIdentity("test", "owner", "Owner")],
            CurrentEnvironmentRevisionId: RevisionId.New(),
            CurrentStateRevisionId: RevisionId.New());

    private sealed class RecordingWorldStorage : IWorldStorage
    {
        public RecordingWorldStorage(params World[] worlds)
        {
            foreach (var world in worlds)
            {
                Worlds[world.Id] = world;
            }
        }

        public Dictionary<WorldId, World> Worlds { get; } = [];

        public Task SaveWorldAsync(World world, CancellationToken cancellationToken = default)
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
            => throw new NotSupportedException();

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

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
}
