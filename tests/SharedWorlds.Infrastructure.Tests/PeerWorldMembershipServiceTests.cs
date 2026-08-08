using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Sessions;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerWorldMembershipServiceTests
{
    [Fact]
    public async Task AddMember_PersistsCanonicalMembershipOnlyForActiveHolder()
    {
        var storage = new InMemoryWorldStorage();
        var fences = new InMemoryFenceStore();
        var holder = new UserIdentity("steam", "1001", "Holder");
        var member = new UserIdentity("steam", "1002", "Member");
        var world = SeedWorld(storage, holder);
        SeedActiveFence(fences, world, holder);
        var service = new PeerWorldMembershipService(storage, fences);

        var updated = await service.AddMemberAsync(
            world.Id,
            holder,
            member);

        Assert.Equal(2, updated.Members.Count);
        Assert.Contains(
            updated.Members,
            candidate => candidate.ExternalId == member.ExternalId);
        Assert.Equal(world.PeerAuthority, updated.PeerAuthority);
        Assert.Equal(world.CurrentStateRevisionId, updated.CurrentStateRevisionId);
        Assert.Equal(1, storage.SaveCount);
    }

    [Fact]
    public async Task AddMember_DuplicateStableIdentityIsIdempotentEvenIfDisplayNameChanged()
    {
        var storage = new InMemoryWorldStorage();
        var fences = new InMemoryFenceStore();
        var holder = new UserIdentity("steam", "1001", "Holder");
        var member = new UserIdentity("steam", "1002", "Original Name");
        var world = SeedWorld(storage, holder) with
        {
            Members = [holder, member]
        };
        storage.Worlds[world.Id] = world;
        SeedActiveFence(fences, world, holder);
        var service = new PeerWorldMembershipService(storage, fences);

        var result = await service.AddMemberAsync(
            world.Id,
            holder,
            new UserIdentity("STEAM", "1002", "Renamed Persona"));

        Assert.Same(world, result);
        Assert.Equal(2, result.Members.Count);
        Assert.Equal(0, storage.SaveCount);
    }

    [Fact]
    public async Task AddMember_RejectsCanonicalNonHolderBeforeMutation()
    {
        var storage = new InMemoryWorldStorage();
        var fences = new InMemoryFenceStore();
        var holder = new UserIdentity("steam", "1001", "Holder");
        var other = new UserIdentity("steam", "1002", "Other");
        var world = SeedWorld(storage, holder) with
        {
            Members = [holder, other]
        };
        storage.Worlds[world.Id] = world;
        fences.Records[world.Id] = new PeerAuthorityFence(
            world.Id,
            holder,
            1,
            world.CurrentStateRevisionId!.Value,
            PeerAuthorityFenceState.Observed,
            DateTimeOffset.UtcNow);
        var service = new PeerWorldMembershipService(storage, fences);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.AddMemberAsync(
                world.Id,
                other,
                new UserIdentity("steam", "1003", "New")));

        Assert.Equal(0, storage.SaveCount);
        Assert.Equal(2, storage.Worlds[world.Id].Members.Count);
    }

    [Fact]
    public async Task AddMember_RejectsHolderWhenDurableFenceIsMissingOrNotActive()
    {
        var storage = new InMemoryWorldStorage();
        var fences = new InMemoryFenceStore();
        var holder = new UserIdentity("steam", "1001", "Holder");
        var world = SeedWorld(storage, holder);
        var service = new PeerWorldMembershipService(storage, fences);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.AddMemberAsync(
                world.Id,
                holder,
                new UserIdentity("steam", "1002", "New")));

        fences.Records[world.Id] = new PeerAuthorityFence(
            world.Id,
            holder,
            1,
            world.CurrentStateRevisionId!.Value,
            PeerAuthorityFenceState.Relinquishing,
            DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.AddMemberAsync(
                world.Id,
                holder,
                new UserIdentity("steam", "1002", "New")));

        Assert.Equal(0, storage.SaveCount);
    }

    [Fact]
    public async Task AddMember_EnforcesBoundedCanonicalMembership()
    {
        var storage = new InMemoryWorldStorage();
        var fences = new InMemoryFenceStore();
        var holder = new UserIdentity("steam", "1", "Holder");
        var members = Enumerable.Range(1, PeerWorldMembershipService.MaximumPeerMembers)
            .Select(index => new UserIdentity("steam", index.ToString(), $"User {index}"))
            .ToArray();
        var world = SeedWorld(storage, holder) with { Members = members };
        storage.Worlds[world.Id] = world;
        SeedActiveFence(fences, world, holder);
        var service = new PeerWorldMembershipService(storage, fences);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddMemberAsync(
                world.Id,
                holder,
                new UserIdentity("steam", "9999", "Overflow")));

        Assert.Equal(0, storage.SaveCount);
        Assert.Equal(
            PeerWorldMembershipService.MaximumPeerMembers,
            storage.Worlds[world.Id].Members.Count);
    }

    [Fact]
    public async Task AddMember_SaveFailureDoesNotAlterStoredWorldOrAuthorityFence()
    {
        var storage = new InMemoryWorldStorage { FailSave = true };
        var fences = new InMemoryFenceStore();
        var holder = new UserIdentity("steam", "1001", "Holder");
        var world = SeedWorld(storage, holder);
        SeedActiveFence(fences, world, holder);
        var service = new PeerWorldMembershipService(storage, fences);

        await Assert.ThrowsAsync<IOException>(() =>
            service.AddMemberAsync(
                world.Id,
                holder,
                new UserIdentity("steam", "1002", "New")));

        Assert.Single(storage.Worlds[world.Id].Members);
        var fence = await fences.LoadAsync(world.Id);
        Assert.NotNull(fence);
        Assert.Equal(PeerAuthorityFenceState.Active, fence.State);
        Assert.Equal((ulong)1, fence.Generation);
    }

    private static World SeedWorld(
        InMemoryWorldStorage storage,
        UserIdentity holder)
    {
        var world = new World(
            WorldId.New(),
            "Shared World",
            "fake",
            [holder],
            RevisionId.New(),
            RevisionId.New())
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = new WorldPeerAuthority(holder, 1)
        };
        storage.Worlds[world.Id] = world;
        return world;
    }

    private static void SeedActiveFence(
        InMemoryFenceStore fences,
        World world,
        UserIdentity holder)
        => fences.Records[world.Id] = new PeerAuthorityFence(
            world.Id,
            holder,
            world.PeerAuthority!.Generation,
            world.CurrentStateRevisionId!.Value,
            PeerAuthorityFenceState.Active,
            DateTimeOffset.UtcNow);

    private sealed class InMemoryFenceStore : IPeerAuthorityFenceStore
    {
        public Dictionary<WorldId, PeerAuthorityFence> Records { get; } = [];

        public Task<PeerAuthorityFence?> LoadAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.TryGetValue(worldId, out var fence);
            return Task.FromResult(fence);
        }

        public Task SaveAsync(
            PeerAuthorityFence fence,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records[fence.WorldId] = fence;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryWorldStorage : IWorldStorage
    {
        public Dictionary<WorldId, World> Worlds { get; } = [];
        public int SaveCount { get; private set; }
        public bool FailSave { get; set; }

        public Task SaveWorldAsync(
            World world,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailSave)
            {
                throw new IOException("Injected World metadata save failure.");
            }

            Worlds[world.Id] = world;
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task<World?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Worlds.TryGetValue(worldId, out var world);
            return Task.FromResult(world);
        }

        public Task StoreEnvironmentRevisionAsync(EnvironmentRevision revision, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(WorldId worldId, RevisionId revisionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task StoreRevisionAsync(StateRevision revision, Stream package, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<StateRevision?> LoadStateRevisionAsync(WorldId worldId, RevisionId revisionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<Stream> OpenRevisionAsync(WorldId worldId, RevisionId revisionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
