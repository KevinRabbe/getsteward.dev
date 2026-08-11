using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Sessions;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerWorldMemberRemovalServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"steward-peer-remove-{Guid.NewGuid():N}");

    [Fact]
    public async Task InactiveWorldRemovesRemoteMemberAndPreservesAuthorityTuple()
    {
        var fixture = await CreateFixtureAsync();
        var service = new PeerWorldMemberRemovalService(
            fixture.Storage,
            fixture.Fences,
            fixture.Lobby);

        var updated = await service.RemoveMemberAsync(
            fixture.World.Id,
            fixture.Holder,
            fixture.Member);

        Assert.Single(updated.Members);
        Assert.Equal(fixture.Holder.ExternalId, updated.Members[0].ExternalId);
        Assert.NotNull(updated.PeerAuthority);
        Assert.Equal(fixture.World.PeerAuthority, updated.PeerAuthority);
        Assert.Equal(fixture.World.CurrentStateRevisionId, updated.CurrentStateRevisionId);
        Assert.Equal(1, fixture.Lobby.GetCalls);
    }

    [Fact]
    public async Task LiveLobbyRefusesRemovalBeforeCanonicalMutation()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Lobby.Snapshot = new PeerWorldLobbySnapshot(
            fixture.World.Id,
            fixture.Holder,
            OwnerConfirmed: true,
            AuthorityGeneration: fixture.World.PeerAuthority!.Generation,
            RequestedHost: null,
            LastCommittedRevision: fixture.World.CurrentStateRevisionId,
            UpdatedAt: DateTimeOffset.UtcNow);
        var service = new PeerWorldMemberRemovalService(
            fixture.Storage,
            fixture.Fences,
            fixture.Lobby);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RemoveMemberAsync(
                fixture.World.Id,
                fixture.Holder,
                fixture.Member));

        Assert.Contains("Stop hosting", exception.Message, StringComparison.Ordinal);
        var unchanged = await fixture.Storage.LoadWorldAsync(fixture.World.Id);
        Assert.NotNull(unchanged);
        Assert.Equal(2, unchanged.Members.Count);
        Assert.Contains(
            unchanged.Members,
            member => member.ExternalId == fixture.Member.ExternalId);
    }

    [Fact]
    public async Task HolderCannotRemoveItself()
    {
        var fixture = await CreateFixtureAsync();
        var service = new PeerWorldMemberRemovalService(
            fixture.Storage,
            fixture.Fences,
            fixture.Lobby);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RemoveMemberAsync(
                fixture.World.Id,
                fixture.Holder,
                fixture.Holder));

        Assert.Equal(0, fixture.Lobby.GetCalls);
    }

    [Fact]
    public async Task StaleFenceRefusesRemovalBeforeLobbyOrWorldMutation()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Fences.Record = fixture.Fences.Record! with
        {
            Generation = fixture.Fences.Record!.Generation + 1
        };
        var service = new PeerWorldMemberRemovalService(
            fixture.Storage,
            fixture.Fences,
            fixture.Lobby);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.RemoveMemberAsync(
                fixture.World.Id,
                fixture.Holder,
                fixture.Member));

        Assert.Equal(0, fixture.Lobby.GetCalls);
        var unchanged = await fixture.Storage.LoadWorldAsync(fixture.World.Id);
        Assert.Equal(2, unchanged!.Members.Count);
    }

    [Fact]
    public async Task AlreadyAbsentMemberIsIdempotentWithoutLobbyDependency()
    {
        var fixture = await CreateFixtureAsync();
        var absent = new UserIdentity("steam", "9009", "Absent");
        var service = new PeerWorldMemberRemovalService(
            fixture.Storage,
            fixture.Fences,
            fixture.Lobby);

        var unchanged = await service.RemoveMemberAsync(
            fixture.World.Id,
            fixture.Holder,
            absent);

        Assert.Equal(fixture.World.Members, unchanged.Members);
        Assert.Equal(0, fixture.Lobby.GetCalls);
    }

    private async Task<Fixture> CreateFixtureAsync()
    {
        Directory.CreateDirectory(_root);
        var storage = new LocalWorldStorage(
            Path.Combine(_root, Guid.NewGuid().ToString("N")));
        var holder = new UserIdentity("steam", "7001", "Holder");
        var member = new UserIdentity("steam", "7002", "Member");
        var stateRevision = RevisionId.New();
        var world = new World(
            WorldId.New(),
            "Removal World",
            "fake",
            [holder, member],
            RevisionId.New(),
            stateRevision)
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = new WorldPeerAuthority(holder, 12)
        };
        await storage.SaveWorldAsync(world);

        var fences = new TestFenceStore
        {
            Record = new PeerAuthorityFence(
                world.Id,
                holder,
                12,
                stateRevision,
                PeerAuthorityFenceState.Active,
                DateTimeOffset.UtcNow)
        };
        return new Fixture(
            storage,
            fences,
            new TestLobby(),
            world,
            holder,
            member);
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
        LocalWorldStorage Storage,
        TestFenceStore Fences,
        TestLobby Lobby,
        World World,
        UserIdentity Holder,
        UserIdentity Member);

    private sealed class TestFenceStore : IPeerAuthorityFenceStore
    {
        public PeerAuthorityFence? Record { get; set; }

        public Task<PeerAuthorityFence?> LoadAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                Record?.WorldId == worldId
                    ? Record
                    : null);
        }

        public Task SaveAsync(
            PeerAuthorityFence fence,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class TestLobby : IPeerWorldLobby
    {
        public PeerWorldLobbySnapshot? Snapshot { get; set; }
        public int GetCalls { get; private set; }

        public Task<PeerWorldLobbySnapshot?> GetAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls++;
            return Task.FromResult(
                Snapshot?.WorldId == worldId
                    ? Snapshot
                    : null);
        }

        public Task<PeerWorldLobbySnapshot> CreateOrGetAsync(
            WorldId worldId,
            UserIdentity proposedOwner,
            ulong authorityGeneration,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PeerWorldLobbySnapshot> RequestHandoffAsync(
            WorldId worldId,
            UserIdentity expectedOwner,
            ulong expectedAuthorityGeneration,
            UserIdentity requestedHost,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PeerWorldLobbySnapshot> TransferOwnershipAsync(
            WorldId worldId,
            UserIdentity expectedOwner,
            ulong expectedAuthorityGeneration,
            UserIdentity newOwner,
            ulong newAuthorityGeneration,
            RevisionId committedRevision,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task LeaveAsync(
            WorldId worldId,
            UserIdentity expectedOwner,
            ulong expectedAuthorityGeneration,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
