using System.Text;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Core.Tests;

public sealed class WorldHistoryRetentionServiceTests
{
    [Fact]
    public async Task Plan_ProtectsNewestCurrentAndCheckpointPayloads()
    {
        var storage = new RetentionStorage();
        var seeded = SeedHistory(storage, revisionCount: 5);
        var checkpointedId = seeded.Revisions[3].Id;
        storage.World = seeded.World with
        {
            Checkpoints =
            [
                new WorldCheckpoint(
                    checkpointedId,
                    "Keep this",
                    DateTimeOffset.UtcNow)
            ]
        };
        storage.AvailablePayloads.Remove(seeded.Revisions[4].Id);
        var service = new WorldHistoryRetentionService(storage);

        var plan = await service.PlanAsync(storage.World, keepNewestPayloads: 2);

        Assert.Equal([seeded.Revisions[2].Id], plan.EvictionCandidates);
        Assert.Equal(1, plan.AlreadyUnavailableCount);
        Assert.False(plan.HasOlderUnscannedHistory);
    }

    [Fact]
    public async Task Apply_RechecksCheckpointAddedAfterPlanning()
    {
        var storage = new RetentionStorage();
        var seeded = SeedHistory(storage, revisionCount: 4);
        var service = new WorldHistoryRetentionService(storage);
        var plan = await service.PlanAsync(seeded.World, keepNewestPayloads: 1);
        var protectedAfterPlan = plan.EvictionCandidates[0];
        storage.World = seeded.World with
        {
            Checkpoints =
            [
                new WorldCheckpoint(
                    protectedAfterPlan,
                    "Added later",
                    DateTimeOffset.UtcNow)
            ]
        };

        var result = await service.ApplyAsync(plan);

        Assert.Equal(2, result.EvictedPayloads);
        Assert.Equal(1, result.NewlyProtectedPayloads);
        Assert.Contains(protectedAfterPlan, storage.AvailablePayloads);
    }

    [Fact]
    public async Task Apply_PreservesRevisionMetadataAndParentChain()
    {
        var storage = new RetentionStorage();
        var seeded = SeedHistory(storage, revisionCount: 3);
        var service = new WorldHistoryRetentionService(storage);
        var plan = await service.PlanAsync(seeded.World, keepNewestPayloads: 1);

        var result = await service.ApplyAsync(plan);
        var history = await new WorldHistoryService(storage).GetHistoryAsync(seeded.World);

        Assert.Equal(2, result.EvictedPayloads);
        Assert.Equal(seeded.Revisions.Select(revision => revision.Id),
            history.Revisions.Select(revision => revision.Id));
        Assert.Equal(3, storage.Revisions.Count);
        Assert.Single(storage.AvailablePayloads);
    }

    [Fact]
    public async Task Apply_IsIdempotentWhenPayloadsAreAlreadyAbsent()
    {
        var storage = new RetentionStorage();
        var seeded = SeedHistory(storage, revisionCount: 3);
        var service = new WorldHistoryRetentionService(storage);
        var plan = await service.PlanAsync(seeded.World, keepNewestPayloads: 1);

        await service.ApplyAsync(plan);
        var second = await service.ApplyAsync(plan);

        Assert.Equal(0, second.EvictedPayloads);
        Assert.Equal(2, second.AlreadyUnavailablePayloads);
    }

    [Fact]
    public async Task Apply_RejectsCandidateThatIsNoLongerCanonical()
    {
        var storage = new RetentionStorage();
        var seeded = SeedHistory(storage, revisionCount: 3);
        var service = new WorldHistoryRetentionService(storage);
        var plan = await service.PlanAsync(seeded.World, keepNewestPayloads: 1);
        storage.World = seeded.World with
        {
            CurrentStateRevisionId = seeded.Revisions[0].Id
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyAsync(plan));
        Assert.Equal(3, storage.AvailablePayloads.Count);
    }

    [Fact]
    public async Task Plan_RejectsZeroRecentPayloadWindow()
    {
        var storage = new RetentionStorage();
        var seeded = SeedHistory(storage, revisionCount: 1);
        var service = new WorldHistoryRetentionService(storage);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.PlanAsync(seeded.World, keepNewestPayloads: 0));
    }

    private static SeededHistory SeedHistory(RetentionStorage storage, int revisionCount)
    {
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var user = new UserIdentity("local", "tester", "Tester");
        var revisionsOldestFirst = new List<StateRevision>();
        RevisionId? parentId = null;

        for (var index = 0; index < revisionCount; index++)
        {
            var revision = new StateRevision(
                RevisionId.New(),
                worldId,
                parentId,
                DateTimeOffset.UtcNow.AddMinutes(index),
                user,
                "fake",
                $"package-{index}",
                environmentId);
            revisionsOldestFirst.Add(revision);
            storage.Revisions[(worldId, revision.Id)] = revision;
            storage.AvailablePayloads.Add(revision.Id);
            parentId = revision.Id;
        }

        var world = new World(
            worldId,
            "Retention World",
            "fake",
            [user],
            environmentId,
            parentId);
        storage.World = world;

        return new SeededHistory(world, revisionsOldestFirst.AsEnumerable().Reverse().ToArray());
    }

    private sealed record SeededHistory(World World, IReadOnlyList<StateRevision> Revisions);

    private sealed class RetentionStorage : IWorldStorage
    {
        public World World { get; set; } = null!;
        public Dictionary<(WorldId, RevisionId), StateRevision> Revisions { get; } = [];
        public HashSet<RevisionId> AvailablePayloads { get; } = [];

        public Task SaveWorldAsync(World world, CancellationToken cancellationToken = default)
        {
            World = world;
            return Task.CompletedTask;
        }

        public Task<World?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<World?>(World.Id == worldId ? World : null);

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
            using var sink = new MemoryStream();
            await package.CopyToAsync(sink, cancellationToken);
            Revisions[(revision.WorldId, revision.Id)] = revision;
            AvailablePayloads.Add(revision.Id);
        }

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            Revisions.TryGetValue((worldId, revisionId), out var revision);
            return Task.FromResult(revision);
        }

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            if (!AvailablePayloads.Contains(revisionId))
            {
                throw new FileNotFoundException();
            }

            Stream stream = new MemoryStream(Encoding.UTF8.GetBytes("state"));
            return Task.FromResult(stream);
        }

        public Task<bool> IsRevisionPayloadAvailableAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(AvailablePayloads.Contains(revisionId));

        public Task<bool> EvictRevisionPayloadAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(AvailablePayloads.Remove(revisionId));
    }
}
