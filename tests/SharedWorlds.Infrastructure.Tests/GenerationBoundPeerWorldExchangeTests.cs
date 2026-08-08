using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Sessions;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class GenerationBoundPeerWorldExchangeTests
{
    [Fact]
    public async Task Bootstrap_ForwardsOnlyCurrentConfirmedLobbyGeneration()
    {
        var host = new UserIdentity("steam", "host");
        var target = new UserIdentity("steam", "target");
        var worldId = WorldId.New();
        var lobby = new StubLobby(new PeerWorldLobbySnapshot(
            worldId,
            host,
            OwnerConfirmed: true,
            AuthorityGeneration: 4,
            RequestedHost: null,
            LastCommittedRevision: null,
            UpdatedAt: DateTimeOffset.UtcNow));
        var inner = new RecordingExchange();
        var exchange = new GenerationBoundPeerWorldExchange(
            lobby,
            host,
            inner,
            inner);
        var offer = Offer(worldId, host, generation: 4);

        await exchange.TransferBootstrapAsync(
            target,
            offer,
            new MemoryStream([1, 2, 3]));

        Assert.Equal(1, inner.BootstrapCount);
        Assert.Equal(0, inner.HandoffCount);
    }

    [Fact]
    public async Task Bootstrap_RejectsStaleOfferGenerationBeforeInnerTransport()
    {
        var host = new UserIdentity("steam", "host");
        var target = new UserIdentity("steam", "target");
        var worldId = WorldId.New();
        var lobby = new StubLobby(new PeerWorldLobbySnapshot(
            worldId,
            host,
            true,
            5,
            null,
            null,
            DateTimeOffset.UtcNow));
        var inner = new RecordingExchange();
        var exchange = new GenerationBoundPeerWorldExchange(
            lobby,
            host,
            inner,
            inner);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            exchange.TransferBootstrapAsync(
                target,
                Offer(worldId, host, generation: 4),
                new MemoryStream([1])));

        Assert.Equal(0, inner.BootstrapCount);
    }

    [Fact]
    public async Task Handoff_ForwardsOnlyProspectiveNextGenerationAndMatchingTarget()
    {
        var host = new UserIdentity("steam", "host");
        var target = new UserIdentity("steam", "target");
        var worldId = WorldId.New();
        var lobby = new StubLobby(new PeerWorldLobbySnapshot(
            worldId,
            host,
            true,
            7,
            target,
            null,
            DateTimeOffset.UtcNow));
        var inner = new RecordingExchange();
        var exchange = new GenerationBoundPeerWorldExchange(
            lobby,
            host,
            inner,
            inner);

        await exchange.TransferAsync(
            target,
            Offer(worldId, target, generation: 8),
            new MemoryStream([4, 5]));

        Assert.Equal(1, inner.HandoffCount);
        Assert.Equal(0, inner.BootstrapCount);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    public async Task Handoff_RejectsNonNextGenerationBeforeInnerTransport(ulong offeredGeneration)
    {
        var host = new UserIdentity("steam", "host");
        var target = new UserIdentity("steam", "target");
        var worldId = WorldId.New();
        var lobby = new StubLobby(new PeerWorldLobbySnapshot(
            worldId,
            host,
            true,
            7,
            target,
            null,
            DateTimeOffset.UtcNow));
        var inner = new RecordingExchange();
        var exchange = new GenerationBoundPeerWorldExchange(
            lobby,
            host,
            inner,
            inner);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            exchange.TransferAsync(
                target,
                Offer(worldId, target, offeredGeneration),
                new MemoryStream([1])));

        Assert.Equal(0, inner.HandoffCount);
    }

    [Fact]
    public async Task AnyTransfer_RejectsUnconfirmedOrRemoteOwnedLobby()
    {
        var local = new UserIdentity("steam", "local");
        var remote = new UserIdentity("steam", "remote");
        var target = new UserIdentity("steam", "target");
        var worldId = WorldId.New();
        var inner = new RecordingExchange();

        foreach (var snapshot in new[]
                 {
                     new PeerWorldLobbySnapshot(
                         worldId,
                         local,
                         false,
                         3,
                         null,
                         null,
                         DateTimeOffset.UtcNow),
                     new PeerWorldLobbySnapshot(
                         worldId,
                         remote,
                         true,
                         3,
                         null,
                         null,
                         DateTimeOffset.UtcNow)
                 })
        {
            var exchange = new GenerationBoundPeerWorldExchange(
                new StubLobby(snapshot),
                local,
                inner,
                inner);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                exchange.TransferBootstrapAsync(
                    target,
                    Offer(worldId, local, 3),
                    new MemoryStream([1])));
        }

        Assert.Equal(0, inner.BootstrapCount);
    }

    private static PeerWorldRevisionOffer Offer(
        WorldId worldId,
        UserIdentity authorityHolder,
        ulong generation)
    {
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();
        var world = new World(
            worldId,
            "World",
            "fake",
            [authorityHolder],
            environmentId,
            stateId)
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = new WorldPeerAuthority(authorityHolder, generation)
        };
        var environment = new EnvironmentRevision(
            environmentId,
            worldId,
            null,
            DateTimeOffset.UtcNow,
            authorityHolder,
            new SharedWorlds.Core.Environment.EnvironmentManifest(
                1,
                "fake",
                "1",
                [],
                new Dictionary<string, string>()));
        var state = new StateRevision(
            stateId,
            worldId,
            null,
            DateTimeOffset.UtcNow,
            authorityHolder,
            "fake",
            "package",
            environmentId);
        return new PeerWorldRevisionOffer(
            world,
            environment,
            state,
            0,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([])));
    }

    private sealed class RecordingExchange :
        IPeerWorldRevisionExchange,
        IPeerWorldBootstrapExchange
    {
        public int HandoffCount { get; private set; }
        public int BootstrapCount { get; private set; }

        public Task<PeerWorldRevisionReceipt> TransferAsync(
            UserIdentity targetHost,
            PeerWorldRevisionOffer offer,
            Stream statePayload,
            CancellationToken cancellationToken = default)
        {
            HandoffCount++;
            return Task.FromResult(new PeerWorldRevisionReceipt(
                offer.World.Id,
                offer.StateRevision.Id,
                offer.PayloadLength,
                offer.PayloadSha256));
        }

        public Task<PeerWorldRevisionReceipt> TransferBootstrapAsync(
            UserIdentity targetMember,
            PeerWorldRevisionOffer offer,
            Stream statePayload,
            CancellationToken cancellationToken = default)
        {
            BootstrapCount++;
            return Task.FromResult(new PeerWorldRevisionReceipt(
                offer.World.Id,
                offer.StateRevision.Id,
                offer.PayloadLength,
                offer.PayloadSha256));
        }
    }

    private sealed class StubLobby : IPeerWorldLobby
    {
        private readonly PeerWorldLobbySnapshot _snapshot;

        public StubLobby(PeerWorldLobbySnapshot snapshot)
            => _snapshot = snapshot;

        public Task<PeerWorldLobbySnapshot?> GetAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<PeerWorldLobbySnapshot?>(
                _snapshot.WorldId == worldId ? _snapshot : null);

        public Task<PeerWorldLobbySnapshot> CreateOrGetAsync(
            WorldId worldId,
            UserIdentity proposedOwner,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PeerWorldLobbySnapshot> RequestHandoffAsync(
            WorldId worldId,
            UserIdentity expectedOwner,
            UserIdentity requestedHost,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PeerWorldLobbySnapshot> TransferOwnershipAsync(
            WorldId worldId,
            UserIdentity expectedOwner,
            UserIdentity newOwner,
            RevisionId committedRevision,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task LeaveAsync(
            WorldId worldId,
            UserIdentity expectedOwner,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
