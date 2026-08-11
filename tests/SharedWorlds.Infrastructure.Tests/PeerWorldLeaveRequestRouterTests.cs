using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Sessions;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerWorldLeaveRequestRouterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"steward-peer-leave-{Guid.NewGuid():N}");

    [Fact]
    public async Task AuthenticatedRequesterIsRevokedAndRemovedBeforeAcknowledgement()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Lobby.Snapshot = LiveLobby(fixture, requestedHost: null);
        using var revocations = new PeerWorldLiveMemberRevocationRegistry();
        using var mutations = new PeerWorldLiveAuthorityMutationGate();
        var existingToken = revocations.GetCancellationToken(
            fixture.World.Id,
            fixture.World.PeerAuthority!.Generation,
            fixture.Member);
        var removal = new PeerWorldMemberRemovalService(
            fixture.Storage,
            fixture.Fences,
            fixture.Lobby,
            revocations,
            mutations);
        var router = new PeerWorldLeaveRequestRouter(
            fixture.Storage,
            fixture.Lobby,
            removal,
            fixture.Holder);

        var result = await router.HandleAsync(
            fixture.Member,
            new PeerWorldLeaveRequest(
                fixture.World.Id,
                fixture.World.PeerAuthority.Generation));

        Assert.Equal(fixture.World.Id, result.WorldId);
        Assert.Equal(fixture.World.PeerAuthority.Generation, result.AuthorityGeneration);
        Assert.Equal(fixture.World.CurrentStateRevisionId, result.CurrentStateRevisionId);
        Assert.True(existingToken.IsCancellationRequested);
        Assert.True(revocations.IsRevoked(
            fixture.World.Id,
            fixture.World.PeerAuthority.Generation,
            fixture.Member));

        var stored = await fixture.Storage.LoadWorldAsync(fixture.World.Id);
        Assert.NotNull(stored);
        Assert.Contains(
            stored.Members,
            candidate => candidate.ExternalId == fixture.Holder.ExternalId);
        Assert.DoesNotContain(
            stored.Members,
            candidate => candidate.ExternalId == fixture.Member.ExternalId);
        Assert.Equal(fixture.World.PeerAuthority, stored.PeerAuthority);
        Assert.Equal(fixture.World.CurrentStateRevisionId, stored.CurrentStateRevisionId);
    }

    [Fact]
    public async Task HolderCannotUseMemberLeaveRequest()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Lobby.Snapshot = LiveLobby(fixture, requestedHost: null);
        using var revocations = new PeerWorldLiveMemberRevocationRegistry();
        using var mutations = new PeerWorldLiveAuthorityMutationGate();
        var router = CreateRouter(fixture, revocations, mutations);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.HandleAsync(
                fixture.Holder,
                new PeerWorldLeaveRequest(
                    fixture.World.Id,
                    fixture.World.PeerAuthority!.Generation)));

        Assert.Contains("holder", exception.Message, StringComparison.OrdinalIgnoreCase);
        var stored = await fixture.Storage.LoadWorldAsync(fixture.World.Id);
        Assert.Equal(2, stored!.Members.Count);
    }

    [Fact]
    public async Task DelayedGenerationCannotRemoveMemberFromLaterAuthorityState()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Lobby.Snapshot = LiveLobby(fixture, requestedHost: null);
        using var revocations = new PeerWorldLiveMemberRevocationRegistry();
        using var mutations = new PeerWorldLiveAuthorityMutationGate();
        var router = CreateRouter(fixture, revocations, mutations);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.HandleAsync(
                fixture.Member,
                new PeerWorldLeaveRequest(
                    fixture.World.Id,
                    fixture.World.PeerAuthority!.Generation - 1)));

        Assert.Contains("generation", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(revocations.IsRevoked(
            fixture.World.Id,
            fixture.World.PeerAuthority!.Generation,
            fixture.Member));
        var stored = await fixture.Storage.LoadWorldAsync(fixture.World.Id);
        Assert.Contains(
            stored!.Members,
            candidate => candidate.ExternalId == fixture.Member.ExternalId);
    }

    [Fact]
    public async Task HandoffBlocksLeaveBeforeRevocationOrCanonicalMutation()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Lobby.Snapshot = LiveLobby(fixture, requestedHost: fixture.Member);
        using var revocations = new PeerWorldLiveMemberRevocationRegistry();
        using var mutations = new PeerWorldLiveAuthorityMutationGate();
        var router = CreateRouter(fixture, revocations, mutations);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.HandleAsync(
                fixture.Member,
                new PeerWorldLeaveRequest(
                    fixture.World.Id,
                    fixture.World.PeerAuthority!.Generation)));

        Assert.Contains("handoff", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(revocations.IsRevoked(
            fixture.World.Id,
            fixture.World.PeerAuthority!.Generation,
            fixture.Member));
        var stored = await fixture.Storage.LoadWorldAsync(fixture.World.Id);
        Assert.Contains(
            stored!.Members,
            candidate => candidate.ExternalId == fixture.Member.ExternalId);
    }

    [Fact]
    public async Task AuthenticatedOutsiderCannotNameOrRemoveCanonicalMember()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Lobby.Snapshot = LiveLobby(fixture, requestedHost: null);
        using var revocations = new PeerWorldLiveMemberRevocationRegistry();
        using var mutations = new PeerWorldLiveAuthorityMutationGate();
        var router = CreateRouter(fixture, revocations, mutations);
        var outsider = new UserIdentity("steam", "7999", "Outsider");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            router.HandleAsync(
                outsider,
                new PeerWorldLeaveRequest(
                    fixture.World.Id,
                    fixture.World.PeerAuthority!.Generation)));

        Assert.False(revocations.IsRevoked(
            fixture.World.Id,
            fixture.World.PeerAuthority!.Generation,
            fixture.Member));
        var stored = await fixture.Storage.LoadWorldAsync(fixture.World.Id);
        Assert.Contains(
            stored!.Members,
            candidate => candidate.ExternalId == fixture.Member.ExternalId);
    }

    private static PeerWorldLeaveRequestRouter CreateRouter(
        Fixture fixture,
        PeerWorldLiveMemberRevocationRegistry revocations,
        PeerWorldLiveAuthorityMutationGate mutations)
    {
        var removal = new PeerWorldMemberRemovalService(
            fixture.Storage,
            fixture.Fences,
            fixture.Lobby,
            revocations,
            mutations);
        return new PeerWorldLeaveRequestRouter(
            fixture.Storage,
            fixture.Lobby,
            removal,
            fixture.Holder);
    }

    private static PeerWorldLobbySnapshot LiveLobby(
        Fixture fixture,
        UserIdentity? requestedHost)
        => new(
            fixture.World.Id,
            fixture.Holder,
            OwnerConfirmed: true,
            AuthorityGeneration: fixture.World.PeerAuthority!.Generation,
            RequestedHost: requestedHost,
            LastCommittedRevision: fixture.World.CurrentStateRevisionId,
            UpdatedAt: DateTimeOffset.UtcNow);

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
            "Leave World",
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

        public Task<PeerWorldLobbySnapshot?> GetAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
