using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;
using SharedWorlds.Infrastructure.Sessions;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerGameDatagramBridgeAdmissionTests
{
    [Fact]
    public async Task Authorize_ReturnsOnlyReadyLocalEndpointForCanonicalMember()
    {
        var worldId = WorldId.New();
        var host = new UserIdentity("steam", "host");
        var member = new UserIdentity("steam", "member");
        var storage = new InMemoryWorldStorage();
        storage.Worlds[worldId] = SharedWorld(worldId, host, member, 5);
        var lobby = new StubLobby(new PeerWorldLobbySnapshot(
            worldId,
            host,
            OwnerConfirmed: true,
            AuthorityGeneration: 5,
            RequestedHost: null,
            LastCommittedRevision: null,
            UpdatedAt: DateTimeOffset.UtcNow));
        var presence = new PeerManagedHostPresenceRegistry();
        await presence.MarkStartingAsync(worldId, host, 5);
        await presence.MarkReadyAsync(
            worldId,
            host,
            5,
            new ManagedHostEndpoint(34197, "game-password"));
        var service = new PeerGameDatagramBridgeAdmissionService(
            lobby,
            storage,
            presence,
            host);

        var grant = await service.AuthorizeAsync(worldId, 5, member);

        Assert.Equal(worldId, grant.WorldId);
        Assert.Equal(host, grant.Host);
        Assert.Equal(member, grant.RemoteMember);
        Assert.Equal((ulong)5, grant.AuthorityGeneration);
        Assert.Equal(34197, grant.HostUdpPort);
        Assert.Equal("game-password", grant.JoinToken);
    }

    [Fact]
    public async Task Authorize_RejectsRemoteOutsideCanonicalMembership()
    {
        var worldId = WorldId.New();
        var host = new UserIdentity("steam", "host");
        var member = new UserIdentity("steam", "member");
        var outsider = new UserIdentity("steam", "outsider");
        var storage = new InMemoryWorldStorage();
        storage.Worlds[worldId] = SharedWorld(worldId, host, member, 2);
        var lobby = Lobby(worldId, host, 2);
        var presence = await ReadyPresence(worldId, host, 2);
        var service = new PeerGameDatagramBridgeAdmissionService(
            lobby,
            storage,
            presence,
            host);

        await Assert.ThrowsAsync<WorldSessionConflictException>(() =>
            service.AuthorizeAsync(worldId, 2, outsider));
    }

    [Fact]
    public async Task Authorize_RejectsLiveGenerationMismatchOrUnconfirmedOwner()
    {
        var worldId = WorldId.New();
        var host = new UserIdentity("steam", "host");
        var member = new UserIdentity("steam", "member");
        var storage = new InMemoryWorldStorage();
        storage.Worlds[worldId] = SharedWorld(worldId, host, member, 4);
        var presence = await ReadyPresence(worldId, host, 4);

        foreach (var snapshot in new[]
                 {
                     new PeerWorldLobbySnapshot(
                         worldId,
                         host,
                         true,
                         3,
                         null,
                         null,
                         DateTimeOffset.UtcNow),
                     new PeerWorldLobbySnapshot(
                         worldId,
                         host,
                         false,
                         4,
                         null,
                         null,
                         DateTimeOffset.UtcNow)
                 })
        {
            var service = new PeerGameDatagramBridgeAdmissionService(
                new StubLobby(snapshot),
                storage,
                presence,
                host);
            await Assert.ThrowsAsync<WorldSessionConflictException>(() =>
                service.AuthorizeAsync(worldId, 4, member));
        }
    }

    [Fact]
    public async Task Authorize_RejectsNewAdmissionDuringHandoff()
    {
        var worldId = WorldId.New();
        var host = new UserIdentity("steam", "host");
        var member = new UserIdentity("steam", "member");
        var nextHost = new UserIdentity("steam", "next");
        var storage = new InMemoryWorldStorage();
        storage.Worlds[worldId] = SharedWorld(worldId, host, member, 6);
        var lobby = new StubLobby(new PeerWorldLobbySnapshot(
            worldId,
            host,
            true,
            6,
            nextHost,
            null,
            DateTimeOffset.UtcNow));
        var service = new PeerGameDatagramBridgeAdmissionService(
            lobby,
            storage,
            await ReadyPresence(worldId, host, 6),
            host);

        await Assert.ThrowsAsync<WorldSessionConflictException>(() =>
            service.AuthorizeAsync(worldId, 6, member));
    }

    [Fact]
    public async Task Authorize_RejectsNotReadyOrMismatchedPresence()
    {
        var worldId = WorldId.New();
        var host = new UserIdentity("steam", "host");
        var member = new UserIdentity("steam", "member");
        var storage = new InMemoryWorldStorage();
        storage.Worlds[worldId] = SharedWorld(worldId, host, member, 7);
        var lobby = Lobby(worldId, host, 7);
        var starting = new PeerManagedHostPresenceRegistry();
        await starting.MarkStartingAsync(worldId, host, 7);
        var service = new PeerGameDatagramBridgeAdmissionService(
            lobby,
            storage,
            starting,
            host);

        await Assert.ThrowsAsync<WorldSessionConflictException>(() =>
            service.AuthorizeAsync(worldId, 7, member));

        var otherGeneration = new PeerManagedHostPresenceRegistry();
        await otherGeneration.MarkStartingAsync(worldId, host, 8);
        await otherGeneration.MarkReadyAsync(
            worldId,
            host,
            8,
            new ManagedHostEndpoint(34197, null));
        service = new PeerGameDatagramBridgeAdmissionService(
            lobby,
            storage,
            otherGeneration,
            host);
        await Assert.ThrowsAsync<WorldSessionConflictException>(() =>
            service.AuthorizeAsync(worldId, 7, member));
    }

    [Fact]
    public async Task Authorize_RequiresConcreteValidUdpPort()
    {
        var worldId = WorldId.New();
        var host = new UserIdentity("steam", "host");
        var member = new UserIdentity("steam", "member");
        var storage = new InMemoryWorldStorage();
        storage.Worlds[worldId] = SharedWorld(worldId, host, member, 1);
        var lobby = Lobby(worldId, host, 1);
        var presence = new PeerManagedHostPresenceRegistry();
        await presence.MarkStartingAsync(worldId, host, 1);
        await presence.MarkReadyAsync(
            worldId,
            host,
            1,
            new ManagedHostEndpoint(null, "token"));
        var service = new PeerGameDatagramBridgeAdmissionService(
            lobby,
            storage,
            presence,
            host);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            service.AuthorizeAsync(worldId, 1, member));
    }

    [Fact]
    public void LoopbackClientConnection_HidesRealHostAddressAndPort()
    {
        var connection = PeerGameDatagramBridgeAdmissionService.CreateLoopbackClientConnection(
            45678,
            "token");

        Assert.Equal("127.0.0.1", connection.Address);
        Assert.Equal(45678, connection.Port);
        Assert.Equal("token", connection.JoinToken);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PeerGameDatagramBridgeAdmissionService.CreateLoopbackClientConnection(0, null));
    }

    private static StubLobby Lobby(
        WorldId worldId,
        UserIdentity host,
        ulong generation)
        => new(new PeerWorldLobbySnapshot(
            worldId,
            host,
            true,
            generation,
            null,
            null,
            DateTimeOffset.UtcNow));

    private static async Task<PeerManagedHostPresenceRegistry> ReadyPresence(
        WorldId worldId,
        UserIdentity host,
        ulong generation)
    {
        var presence = new PeerManagedHostPresenceRegistry();
        await presence.MarkStartingAsync(worldId, host, generation);
        await presence.MarkReadyAsync(
            worldId,
            host,
            generation,
            new ManagedHostEndpoint(34197, "token"));
        return presence;
    }

    private static World SharedWorld(
        WorldId worldId,
        UserIdentity host,
        UserIdentity member,
        ulong generation)
        => new(
            worldId,
            "Peer World",
            "fake",
            [host, member],
            RevisionId.New(),
            RevisionId.New())
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = new WorldPeerAuthority(host, generation)
        };

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

    private sealed class InMemoryWorldStorage : IWorldStorage
    {
        public Dictionary<WorldId, World> Worlds { get; } = [];

        public Task SaveWorldAsync(
            World world,
            CancellationToken cancellationToken = default)
        {
            Worlds[world.Id] = world;
            return Task.CompletedTask;
        }

        public Task<World?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            Worlds.TryGetValue(worldId, out var world);
            return Task.FromResult(world);
        }

        public Task StoreEnvironmentRevisionAsync(
            EnvironmentRevision revision,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<EnvironmentRevision?>(null);

        public Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<StateRevision?>(null);

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream());
    }
}
