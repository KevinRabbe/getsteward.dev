using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Infrastructure.Sessions;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerWorldSessionCoordinatorTests
{
    [Fact]
    public async Task AcquireHost_UsesSinglePeerLobbyOwner()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var worldId = WorldId.New();
        var first = new UserIdentity("steam", "first");
        var second = new UserIdentity("steam", "second");
        var firstCoordinator = new PeerWorldSessionCoordinator(lobby, first);
        var secondCoordinator = new PeerWorldSessionCoordinator(lobby, second);

        var hosted = await firstCoordinator.AcquireHostAsync(worldId, first);

        Assert.Equal(SessionState.Hosting, hosted.State);
        Assert.Equal(first, hosted.ActiveHost);
        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => secondCoordinator.AcquireHostAsync(worldId, second));
    }

    [Fact]
    public async Task GracefulHandoff_TransfersLobbyOnlyAfterCommittedRevision()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var worldId = WorldId.New();
        var first = new UserIdentity("steam", "first");
        var second = new UserIdentity("steam", "second");
        var firstCoordinator = new PeerWorldSessionCoordinator(lobby, first);
        var secondCoordinator = new PeerWorldSessionCoordinator(lobby, second);
        var committedRevision = RevisionId.New();

        await firstCoordinator.AcquireHostAsync(worldId, first);
        await firstCoordinator.RequestHandoffAsync(worldId, second);

        var pending = await firstCoordinator.GetSessionAsync(worldId);
        Assert.Equal(SessionState.HandoffRequested, pending.State);
        Assert.Equal(first, pending.ActiveHost);
        Assert.Equal(second, pending.RequestedHost);

        await firstCoordinator.CompleteHandoffAsync(worldId, second, committedRevision);

        var hostedBySecond = await secondCoordinator.GetSessionAsync(worldId);
        Assert.Equal(SessionState.Hosting, hostedBySecond.State);
        Assert.Equal(second, hostedBySecond.ActiveHost);
        Assert.Null(hostedBySecond.RequestedHost);

        var lobbyState = await lobby.GetAsync(worldId);
        Assert.NotNull(lobbyState);
        Assert.Equal(committedRevision, lobbyState.LastCommittedRevision);
    }

    [Fact]
    public async Task ReleaseHost_ClosesLobbyAndMakesWorldInactive()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var worldId = WorldId.New();
        var host = new UserIdentity("steam", "host");
        var coordinator = new PeerWorldSessionCoordinator(lobby, host);

        await coordinator.AcquireHostAsync(worldId, host);
        await coordinator.ReleaseHostAsync(worldId, host);

        var inactive = await coordinator.GetSessionAsync(worldId);
        Assert.Equal(SessionState.Available, inactive.State);
        Assert.Null(inactive.ActiveHost);
    }

    [Fact]
    public async Task NonOwner_CannotRequestHandoff()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var worldId = WorldId.New();
        var host = new UserIdentity("steam", "host");
        var other = new UserIdentity("steam", "other");
        var next = new UserIdentity("steam", "next");
        var hostCoordinator = new PeerWorldSessionCoordinator(lobby, host);
        var otherCoordinator = new PeerWorldSessionCoordinator(lobby, other);

        await hostCoordinator.AcquireHostAsync(worldId, host);

        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => otherCoordinator.RequestHandoffAsync(worldId, next));
    }

    private sealed class InMemoryPeerWorldLobby : IPeerWorldLobby
    {
        private readonly object _gate = new();
        private readonly Dictionary<WorldId, PeerWorldLobbySnapshot> _worlds = [];

        public Task<PeerWorldLobbySnapshot?> GetAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _worlds.TryGetValue(worldId, out var snapshot);
                return Task.FromResult(snapshot);
            }
        }

        public Task<PeerWorldLobbySnapshot> CreateOrGetAsync(
            WorldId worldId,
            UserIdentity proposedOwner,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_worlds.TryGetValue(worldId, out var existing))
                {
                    return Task.FromResult(existing);
                }

                var created = new PeerWorldLobbySnapshot(
                    worldId,
                    proposedOwner,
                    RequestedHost: null,
                    LastCommittedRevision: null,
                    DateTimeOffset.UtcNow);
                _worlds.Add(worldId, created);
                return Task.FromResult(created);
            }
        }

        public Task<PeerWorldLobbySnapshot> RequestHandoffAsync(
            WorldId worldId,
            UserIdentity expectedOwner,
            UserIdentity requestedHost,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var current = RequireOwned(worldId, expectedOwner);
                var updated = current with
                {
                    RequestedHost = requestedHost,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                _worlds[worldId] = updated;
                return Task.FromResult(updated);
            }
        }

        public Task<PeerWorldLobbySnapshot> TransferOwnershipAsync(
            WorldId worldId,
            UserIdentity expectedOwner,
            UserIdentity newOwner,
            RevisionId committedRevision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var current = RequireOwned(worldId, expectedOwner);
                if (current.RequestedHost != newOwner)
                {
                    throw new WorldSessionConflictException(worldId, "The requested host changed before transfer.");
                }

                var updated = current with
                {
                    Owner = newOwner,
                    RequestedHost = null,
                    LastCommittedRevision = committedRevision,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                _worlds[worldId] = updated;
                return Task.FromResult(updated);
            }
        }

        public Task LeaveAsync(
            WorldId worldId,
            UserIdentity expectedOwner,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _ = RequireOwned(worldId, expectedOwner);
                _worlds.Remove(worldId);
                return Task.CompletedTask;
            }
        }

        private PeerWorldLobbySnapshot RequireOwned(WorldId worldId, UserIdentity expectedOwner)
        {
            if (!_worlds.TryGetValue(worldId, out var current) || current.Owner != expectedOwner)
            {
                throw new WorldSessionConflictException(worldId, "The expected peer-lobby owner is no longer current.");
            }

            return current;
        }
    }
}
