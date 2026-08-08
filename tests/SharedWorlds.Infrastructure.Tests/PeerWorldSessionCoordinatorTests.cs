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
        var firstCoordinator = new PeerWorldSessionCoordinator(
            lobby,
            first,
            new RecordingRevisionTransfer());
        var secondCoordinator = new PeerWorldSessionCoordinator(
            lobby,
            second,
            new RecordingRevisionTransfer());

        var hosted = await firstCoordinator.AcquireHostAsync(worldId, first);

        Assert.Equal(SessionState.Hosting, hosted.State);
        Assert.Equal(first, hosted.ActiveHost);
        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => secondCoordinator.AcquireHostAsync(worldId, second));
    }

    [Fact]
    public async Task GracefulHandoff_VerifiesCommittedRevisionBeforeTransferringLobby()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var transfer = new RecordingRevisionTransfer();
        var worldId = WorldId.New();
        var first = new UserIdentity("steam", "first");
        var second = new UserIdentity("steam", "second");
        var firstCoordinator = new PeerWorldSessionCoordinator(lobby, first, transfer);
        var secondCoordinator = new PeerWorldSessionCoordinator(
            lobby,
            second,
            new RecordingRevisionTransfer());
        var committedRevision = RevisionId.New();

        await firstCoordinator.AcquireHostAsync(worldId, first);
        await firstCoordinator.RequestHandoffAsync(worldId, second);

        var pending = await firstCoordinator.GetSessionAsync(worldId);
        Assert.Equal(SessionState.HandoffRequested, pending.State);
        Assert.Equal(first, pending.ActiveHost);
        Assert.Equal(second, pending.RequestedHost);

        await firstCoordinator.CompleteHandoffAsync(worldId, second, committedRevision);

        Assert.Equal(1, transfer.EnsureCount);
        Assert.Equal(worldId, transfer.LastWorldId);
        Assert.Equal(committedRevision, transfer.LastRevision);
        Assert.Equal(second.ExternalId, transfer.LastTarget?.ExternalId);
        Assert.Equal(1, lobby.TransferOwnershipCount);

        var hostedBySecond = await secondCoordinator.GetSessionAsync(worldId);
        Assert.Equal(SessionState.Hosting, hostedBySecond.State);
        Assert.Equal(second.ExternalId, hostedBySecond.ActiveHost?.ExternalId);
        Assert.Null(hostedBySecond.RequestedHost);

        var lobbyState = await lobby.GetAsync(worldId);
        Assert.NotNull(lobbyState);
        Assert.True(lobbyState.OwnerConfirmed);
        Assert.Equal(committedRevision, lobbyState.LastCommittedRevision);
    }

    [Fact]
    public async Task Handoff_DoesNotTransferOwnership_WhenRevisionTransferFails()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var transfer = new RecordingRevisionTransfer { Fail = true };
        var worldId = WorldId.New();
        var first = new UserIdentity("steam", "first");
        var second = new UserIdentity("steam", "second");
        var coordinator = new PeerWorldSessionCoordinator(lobby, first, transfer);
        var committedRevision = RevisionId.New();

        await coordinator.AcquireHostAsync(worldId, first);
        await coordinator.RequestHandoffAsync(worldId, second);

        await Assert.ThrowsAsync<IOException>(
            () => coordinator.CompleteHandoffAsync(worldId, second, committedRevision));

        Assert.Equal(1, transfer.EnsureCount);
        Assert.Equal(0, lobby.TransferOwnershipCount);
        var current = await lobby.GetAsync(worldId);
        Assert.NotNull(current);
        Assert.Equal(first.ExternalId, current.Owner.ExternalId);
        Assert.Equal(second.ExternalId, current.RequestedHost?.ExternalId);
        Assert.Null(current.LastCommittedRevision);
    }

    [Fact]
    public async Task Handoff_RevalidatesAuthorityAfterRevisionTransfer()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var worldId = WorldId.New();
        var first = new UserIdentity("steam", "first");
        var second = new UserIdentity("steam", "second");
        var third = new UserIdentity("steam", "third");
        var transfer = new RecordingRevisionTransfer
        {
            AfterEnsure = () => lobby.SimulateAutomaticOwnerChange(worldId, third)
        };
        var coordinator = new PeerWorldSessionCoordinator(lobby, first, transfer);

        await coordinator.AcquireHostAsync(worldId, first);
        await coordinator.RequestHandoffAsync(worldId, second);

        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => coordinator.CompleteHandoffAsync(worldId, second, RevisionId.New()));

        Assert.Equal(1, transfer.EnsureCount);
        Assert.Equal(0, lobby.TransferOwnershipCount);
        var current = await lobby.GetAsync(worldId);
        Assert.NotNull(current);
        Assert.False(current.OwnerConfirmed);
        Assert.Equal(third.ExternalId, current.Owner.ExternalId);
    }

    [Fact]
    public async Task AutomaticPlatformOwnerChange_IsRecoveryPendingUntilStewardConfirmsIt()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var worldId = WorldId.New();
        var first = new UserIdentity("steam", "first");
        var second = new UserIdentity("steam", "second");
        var firstCoordinator = new PeerWorldSessionCoordinator(
            lobby,
            first,
            new RecordingRevisionTransfer());
        var secondCoordinator = new PeerWorldSessionCoordinator(
            lobby,
            second,
            new RecordingRevisionTransfer());

        await firstCoordinator.AcquireHostAsync(worldId, first);
        lobby.SimulateAutomaticOwnerChange(worldId, second);

        var observed = await secondCoordinator.GetSessionAsync(worldId);
        Assert.Equal(SessionState.RecoveryPending, observed.State);
        Assert.Equal(second.ExternalId, observed.ActiveHost?.ExternalId);
        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => secondCoordinator.AcquireHostAsync(worldId, second));
    }

    [Fact]
    public async Task ReleaseHost_ClosesLobbyAndMakesWorldInactive()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var worldId = WorldId.New();
        var host = new UserIdentity("steam", "host");
        var coordinator = new PeerWorldSessionCoordinator(
            lobby,
            host,
            new RecordingRevisionTransfer());

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
        var hostCoordinator = new PeerWorldSessionCoordinator(
            lobby,
            host,
            new RecordingRevisionTransfer());
        var otherCoordinator = new PeerWorldSessionCoordinator(
            lobby,
            other,
            new RecordingRevisionTransfer());

        await hostCoordinator.AcquireHostAsync(worldId, host);

        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => otherCoordinator.RequestHandoffAsync(worldId, next));
    }

    private sealed class RecordingRevisionTransfer : IPeerWorldRevisionTransfer
    {
        public int EnsureCount { get; private set; }
        public WorldId? LastWorldId { get; private set; }
        public RevisionId? LastRevision { get; private set; }
        public UserIdentity? LastTarget { get; private set; }
        public bool Fail { get; init; }
        public Action? AfterEnsure { get; init; }

        public Task EnsureAvailableAsync(
            WorldId worldId,
            RevisionId committedStateRevision,
            UserIdentity targetHost,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCount++;
            LastWorldId = worldId;
            LastRevision = committedStateRevision;
            LastTarget = targetHost;
            if (Fail)
            {
                throw new IOException("Injected peer revision transfer failure.");
            }

            AfterEnsure?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryPeerWorldLobby : IPeerWorldLobby
    {
        private readonly object _gate = new();
        private readonly Dictionary<WorldId, PeerWorldLobbySnapshot> _worlds = [];

        public int TransferOwnershipCount { get; private set; }

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
                    OwnerConfirmed: true,
                    RequestedHost: null,
                    LastCommittedRevision: null,
                    UpdatedAt: DateTimeOffset.UtcNow);
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
                if (current.RequestedHost is null ||
                    !SameUser(current.RequestedHost, newOwner))
                {
                    throw new WorldSessionConflictException(worldId, "The requested host changed before transfer.");
                }

                TransferOwnershipCount++;
                var updated = current with
                {
                    Owner = newOwner,
                    OwnerConfirmed = true,
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

        public void SimulateAutomaticOwnerChange(WorldId worldId, UserIdentity newOwner)
        {
            lock (_gate)
            {
                var current = _worlds[worldId];
                _worlds[worldId] = current with
                {
                    Owner = newOwner,
                    OwnerConfirmed = false,
                    RequestedHost = null,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
        }

        private PeerWorldLobbySnapshot RequireOwned(WorldId worldId, UserIdentity expectedOwner)
        {
            if (!_worlds.TryGetValue(worldId, out var current) ||
                !SameUser(current.Owner, expectedOwner) ||
                !current.OwnerConfirmed)
            {
                throw new WorldSessionConflictException(worldId, "The expected peer-lobby owner is no longer current.");
            }

            return current;
        }

        private static bool SameUser(UserIdentity left, UserIdentity right)
            => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
    }
}
