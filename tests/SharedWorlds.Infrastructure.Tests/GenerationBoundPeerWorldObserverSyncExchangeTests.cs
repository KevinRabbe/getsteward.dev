using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Sessions;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class GenerationBoundPeerWorldObserverSyncExchangeTests
{
    [Fact]
    public async Task Transfer_ForwardsOnlyConfirmedCurrentGenerationWithoutHandoff()
    {
        var host = new UserIdentity("steam", "host");
        var target = new UserIdentity("steam", "target");
        var worldId = WorldId.New();
        var inner = new RecordingObserverExchange();
        var exchange = new GenerationBoundPeerWorldObserverSyncExchange(
            new StubLobby(new PeerWorldLobbySnapshot(
                worldId,
                host,
                true,
                6,
                null,
                null,
                DateTimeOffset.UtcNow)),
            host,
            inner);

        await exchange.TransferObserverRevisionAsync(
            target,
            Offer(worldId, host, 6),
            new MemoryStream([1]));

        Assert.Equal(1, inner.TransferCount);
    }

    [Fact]
    public async Task Transfer_RejectsGenerationMismatchBeforeInnerExchange()
    {
        var host = new UserIdentity("steam", "host");
        var target = new UserIdentity("steam", "target");
        var worldId = WorldId.New();
        var inner = new RecordingObserverExchange();
        var exchange = new GenerationBoundPeerWorldObserverSyncExchange(
            new StubLobby(new PeerWorldLobbySnapshot(
                worldId,
                host,
                true,
                6,
                null,
                null,
                DateTimeOffset.UtcNow)),
            host,
            inner);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            exchange.TransferObserverRevisionAsync(
                target,
                Offer(worldId, host, 5),
                new MemoryStream([1])));

        Assert.Equal(0, inner.TransferCount);
    }

    [Fact]
    public async Task Transfer_StopsWhenGracefulHandoffHasStarted()
    {
        var host = new UserIdentity("steam", "host");
        var target = new UserIdentity("steam", "target");
        var nextHost = new UserIdentity("steam", "next");
        var worldId = WorldId.New();
        var inner = new RecordingObserverExchange();
        var exchange = new GenerationBoundPeerWorldObserverSyncExchange(
            new StubLobby(new PeerWorldLobbySnapshot(
                worldId,
                host,
                true,
                6,
                nextHost,
                null,
                DateTimeOffset.UtcNow)),
            host,
            inner);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            exchange.TransferObserverRevisionAsync(
                target,
                Offer(worldId, host, 6),
                new MemoryStream([1])));

        Assert.Equal(0, inner.TransferCount);
    }

    [Fact]
    public async Task Transfer_RejectsUnconfirmedOrDifferentLiveOwner()
    {
        var host = new UserIdentity("steam", "host");
        var other = new UserIdentity("steam", "other");
        var target = new UserIdentity("steam", "target");
        var worldId = WorldId.New();
        var inner = new RecordingObserverExchange();

        foreach (var snapshot in new[]
                 {
                     new PeerWorldLobbySnapshot(
                         worldId,
                         host,
                         false,
                         6,
                         null,
                         null,
                         DateTimeOffset.UtcNow),
                     new PeerWorldLobbySnapshot(
                         worldId,
                         other,
                         true,
                         6,
                         null,
                         null,
                         DateTimeOffset.UtcNow)
                 })
        {
            var exchange = new GenerationBoundPeerWorldObserverSyncExchange(
                new StubLobby(snapshot),
                host,
                inner);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                exchange.TransferObserverRevisionAsync(
                    target,
                    Offer(worldId, host, 6),
                    new MemoryStream([1])));
        }

        Assert.Equal(0, inner.TransferCount);
    }

    private static PeerWorldRevisionOffer Offer(
        WorldId worldId,
        UserIdentity holder,
        ulong generation)
    {
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();
        var world = new World(
            worldId,
            "Observer",
            "fake",
            [holder],
            environmentId,
            stateId)
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = new WorldPeerAuthority(holder, generation)
        };
        var environment = new EnvironmentRevision(
            environmentId,
            worldId,
            null,
            DateTimeOffset.UtcNow,
            holder,
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
            holder,
            "fake",
            "package",
            environmentId);
        return new PeerWorldRevisionOffer(
            world,
            environment,
            state,
            0,
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>())));
    }

    private sealed class RecordingObserverExchange : IPeerWorldObserverSyncExchange
    {
        public int TransferCount { get; private set; }

        public Task<PeerWorldRevisionReceipt> TransferObserverRevisionAsync(
            UserIdentity targetMember,
            PeerWorldRevisionOffer offer,
            Stream statePayload,
            CancellationToken cancellationToken = default)
        {
            TransferCount++;
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
