using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Sessions;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerWorldLiveMemberRemovalServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"steward-peer-live-remove-{Guid.NewGuid():N}");

    [Fact]
    public async Task LiveRemovalRevokesBeforeCanonicalMembershipSave()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Lobby.Snapshot = LiveLobby(fixture, requestedHost: null);
        using var revocations = new PeerWorldLiveMemberRevocationRegistry();
        using var mutations = new PeerWorldLiveAuthorityMutationGate();
        var existingToken = revocations.GetCancellationToken(
            fixture.World.Id,
            fixture.World.PeerAuthority!.Generation,
            fixture.Member);
        var notificationCount = 0;
        var memberWasStillCanonicalAtRevocation = false;
        var tokenWasCanceledAtRevocation = false;
        revocations.Revoked += revocation =>
        {
            notificationCount++;
            tokenWasCanceledAtRevocation = existingToken.IsCancellationRequested;
            var current = fixture.Storage.LoadWorldAsync(revocation.WorldId)
                .GetAwaiter()
                .GetResult();
            memberWasStillCanonicalAtRevocation = current is not null &&
                current.Members.Any(candidate =>
                    candidate.ExternalId == fixture.Member.ExternalId);
        };
        var service = new PeerWorldMemberRemovalService(
            fixture.Storage,
            fixture.Fences,
            fixture.Lobby,
            revocations,
            mutations);

        var updated = await service.RemoveMemberAsync(
            fixture.World.Id,
            fixture.Holder,
            fixture.Member);

        Assert.Equal(1, notificationCount);
        Assert.True(tokenWasCanceledAtRevocation);
        Assert.True(memberWasStillCanonicalAtRevocation);
        Assert.True(existingToken.IsCancellationRequested);
        Assert.True(revocations.IsRevoked(
            fixture.World.Id,
            fixture.World.PeerAuthority.Generation,
            fixture.Member));
        Assert.DoesNotContain(
            updated.Members,
            member => member.ExternalId == fixture.Member.ExternalId);
        Assert.Contains(
            updated.Members,
            member => member.ExternalId == fixture.Holder.ExternalId);
        Assert.Equal(fixture.World.PeerAuthority, updated.PeerAuthority);
        Assert.Equal(fixture.World.CurrentStateRevisionId, updated.CurrentStateRevisionId);
    }

    [Fact]
    public async Task LiveHandoffRefusesRemovalBeforeRevocationOrCanonicalMutation()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Lobby.Snapshot = LiveLobby(fixture, requestedHost: fixture.Member);
        using var revocations = new PeerWorldLiveMemberRevocationRegistry();
        using var mutations = new PeerWorldLiveAuthorityMutationGate();
        var service = new PeerWorldMemberRemovalService(
            fixture.Storage,
            fixture.Fences,
            fixture.Lobby,
            revocations,
            mutations);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RemoveMemberAsync(
                fixture.World.Id,
                fixture.Holder,
                fixture.Member));

        Assert.Contains("handoff", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(revocations.IsRevoked(
            fixture.World.Id,
            fixture.World.PeerAuthority!.Generation,
            fixture.Member));
        var unchanged = await fixture.Storage.LoadWorldAsync(fixture.World.Id);
        Assert.NotNull(unchanged);
        Assert.Contains(
            unchanged.Members,
            member => member.ExternalId == fixture.Member.ExternalId);
    }

    [Fact]
    public async Task IncompleteRuntimeStillRefusesLiveRemoval()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Lobby.Snapshot = LiveLobby(fixture, requestedHost: null);
        var service = new PeerWorldMemberRemovalService(
            fixture.Storage,
            fixture.Fences,
            fixture.Lobby);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RemoveMemberAsync(
                fixture.World.Id,
                fixture.Holder,
                fixture.Member));

        Assert.Contains("complete live member revocation boundary", exception.Message, StringComparison.OrdinalIgnoreCase);
        var unchanged = await fixture.Storage.LoadWorldAsync(fixture.World.Id);
        Assert.Equal(2, unchanged!.Members.Count);
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
            "Live Removal World",
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
