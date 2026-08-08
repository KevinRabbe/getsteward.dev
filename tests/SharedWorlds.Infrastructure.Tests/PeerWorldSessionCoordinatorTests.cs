using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Infrastructure.Sessions;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerWorldSessionCoordinatorTests
{
    [Fact]
    public async Task AcquireHost_UsesPersistentHolderAndSinglePeerLobbyOwner()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var storage = new InMemoryWorldStorage();
        var worldId = WorldId.New();
        var first = new UserIdentity("steam", "first");
        var second = new UserIdentity("steam", "second");
        SeedWorld(storage, worldId, first, [first, second]);
        var firstCoordinator = Coordinator(lobby, storage, first);
        var secondCoordinator = Coordinator(lobby, storage, second);

        var hosted = await firstCoordinator.AcquireHostAsync(worldId, first);

        Assert.Equal(SessionState.Hosting, hosted.State);
        Assert.Equal(first, hosted.ActiveHost);
        Assert.Equal(1, lobby.CreateOrGetCount);
        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => secondCoordinator.AcquireHostAsync(worldId, second));
        Assert.Equal(1, lobby.CreateOrGetCount);
    }

    [Fact]
    public async Task AcquireHost_RejectsCanonicalMemberWhoIsNotPersistentHolder_BeforeCreatingLobby()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var storage = new InMemoryWorldStorage();
        var worldId = WorldId.New();
        var holder = new UserIdentity("steam", "holder");
        var staleMember = new UserIdentity("steam", "stale-member");
        SeedWorld(storage, worldId, holder, [holder, staleMember]);
        var coordinator = Coordinator(lobby, storage, staleMember);

        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => coordinator.AcquireHostAsync(worldId, staleMember));

        Assert.Equal(0, lobby.CreateOrGetCount);
        Assert.Null(await lobby.GetAsync(worldId));
    }

    [Fact]
    public async Task AcquireHost_RejectsSharedWorldWithoutPersistentPeerAuthority()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var storage = new InMemoryWorldStorage();
        var worldId = WorldId.New();
        var user = new UserIdentity("steam", "legacy-owner");
        var stateId = RevisionId.New();
        storage.Worlds[worldId] = new World(
            worldId,
            "Legacy Shared World",
            "fake",
            [user],
            RevisionId.New(),
            stateId)
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = null
        };
        var coordinator = Coordinator(lobby, storage, user);

        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => coordinator.AcquireHostAsync(worldId, user));

        Assert.Equal(0, lobby.CreateOrGetCount);
    }

    [Fact]
    public async Task GracefulHandoff_VerifiesCommittedRevisionThenAdvancesPersistentAndLobbyAuthority()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var storage = new InMemoryWorldStorage();
        var transfer = new RecordingRevisionTransfer();
        var worldId = WorldId.New();
        var first = new UserIdentity("steam", "first");
        var second = new UserIdentity("steam", "second");
        var initialState = RevisionId.New();
        SeedWorld(storage, worldId, first, [first, second], initialState);
        var firstCoordinator = new PeerWorldSessionCoordinator(
            lobby,
            first,
            transfer,
            storage);
        var secondCoordinator = Coordinator(lobby, storage, second);
        var committedRevision = RevisionId.New();

        await firstCoordinator.AcquireHostAsync(worldId, first);
        await firstCoordinator.RequestHandoffAsync(worldId, second);
        storage.Worlds[worldId] = storage.Worlds[worldId] with
        {
            CurrentStateRevisionId = committedRevision
        };

        var pending = await firstCoordinator.GetSessionAsync(worldId);
        Assert.Equal(SessionState.HandoffRequested, pending.State);
        Assert.Equal(first, pending.ActiveHost);
        Assert.Equal(second, pending.RequestedHost);

        await firstCoordinator.CompleteHandoffAsync(
            worldId,
            second,
            committedRevision);

        Assert.Equal(1, transfer.EnsureCount);
        Assert.Equal(worldId, transfer.LastWorldId);
        Assert.Equal(committedRevision, transfer.LastRevision);
        Assert.Equal(second.ExternalId, transfer.LastTarget?.ExternalId);
        Assert.Equal(1, lobby.TransferOwnershipCount);

        var canonical = storage.Worlds[worldId];
        Assert.NotNull(canonical.PeerAuthority);
        Assert.Equal((ulong)2, canonical.PeerAuthority.Generation);
        Assert.Equal(second.ExternalId, canonical.PeerAuthority.Holder.ExternalId);
        Assert.Equal(committedRevision, canonical.CurrentStateRevisionId);

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
    public async Task Handoff_TargetMustAlreadyBeCanonicalMember()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var storage = new InMemoryWorldStorage();
        var worldId = WorldId.New();
        var host = new UserIdentity("steam", "host");
        var outsider = new UserIdentity("steam", "outsider");
        SeedWorld(storage, worldId, host, [host]);
        var coordinator = Coordinator(lobby, storage, host);

        await coordinator.AcquireHostAsync(worldId, host);

        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => coordinator.RequestHandoffAsync(worldId, outsider));
        var current = await lobby.GetAsync(worldId);
        Assert.NotNull(current);
        Assert.Null(current.RequestedHost);
    }

    [Fact]
    public async Task Handoff_DoesNotAdvancePersistentOrLobbyAuthority_WhenRevisionTransferFails()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var storage = new InMemoryWorldStorage();
        var transfer = new RecordingRevisionTransfer { Fail = true };
        var worldId = WorldId.New();
        var first = new UserIdentity("steam", "first");
        var second = new UserIdentity("steam", "second");
        var committedRevision = RevisionId.New();
        SeedWorld(storage, worldId, first, [first, second], committedRevision);
        var coordinator = new PeerWorldSessionCoordinator(
            lobby,
            first,
            transfer,
            storage);

        await coordinator.AcquireHostAsync(worldId, first);
        await coordinator.RequestHandoffAsync(worldId, second);

        await Assert.ThrowsAsync<IOException>(
            () => coordinator.CompleteHandoffAsync(
                worldId,
                second,
                committedRevision));

        Assert.Equal(1, transfer.EnsureCount);
        Assert.Equal(0, lobby.TransferOwnershipCount);
        var canonical = storage.Worlds[worldId];
        Assert.NotNull(canonical.PeerAuthority);
        Assert.Equal((ulong)1, canonical.PeerAuthority.Generation);
        Assert.Equal(first.ExternalId, canonical.PeerAuthority.Holder.ExternalId);
        var current = await lobby.GetAsync(worldId);
        Assert.NotNull(current);
        Assert.Equal(first.ExternalId, current.Owner.ExternalId);
        Assert.Equal(second.ExternalId, current.RequestedHost?.ExternalId);
        Assert.Null(current.LastCommittedRevision);
    }

    [Fact]
    public async Task Handoff_RollsBackPersistentAuthority_WhenSteamRejectsAndOldOwnerIsStillConfirmed()
    {
        var lobby = new InMemoryPeerWorldLobby { FailTransfer = true };
        var storage = new InMemoryWorldStorage();
        var worldId = WorldId.New();
        var first = new UserIdentity("steam", "first");
        var second = new UserIdentity("steam", "second");
        var committedRevision = RevisionId.New();
        SeedWorld(storage, worldId, first, [first, second], committedRevision);
        var coordinator = Coordinator(lobby, storage, first);

        await coordinator.AcquireHostAsync(worldId, first);
        await coordinator.RequestHandoffAsync(worldId, second);

        await Assert.ThrowsAsync<IOException>(
            () => coordinator.CompleteHandoffAsync(
                worldId,
                second,
                committedRevision));

        var canonical = storage.Worlds[worldId];
        Assert.NotNull(canonical.PeerAuthority);
        Assert.Equal((ulong)1, canonical.PeerAuthority.Generation);
        Assert.Equal(first.ExternalId, canonical.PeerAuthority.Holder.ExternalId);
        var current = await lobby.GetAsync(worldId);
        Assert.NotNull(current);
        Assert.True(current.OwnerConfirmed);
        Assert.Equal(first.ExternalId, current.Owner.ExternalId);
    }

    [Fact]
    public async Task Handoff_RevalidatesLiveAuthorityAfterRevisionTransfer()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var storage = new InMemoryWorldStorage();
        var worldId = WorldId.New();
        var first = new UserIdentity("steam", "first");
        var second = new UserIdentity("steam", "second");
        var third = new UserIdentity("steam", "third");
        var committedRevision = RevisionId.New();
        SeedWorld(storage, worldId, first, [first, second, third], committedRevision);
        var transfer = new RecordingRevisionTransfer
        {
            AfterEnsure = () => lobby.SimulateAutomaticOwnerChange(worldId, third)
        };
        var coordinator = new PeerWorldSessionCoordinator(
            lobby,
            first,
            transfer,
            storage);

        await coordinator.AcquireHostAsync(worldId, first);
        await coordinator.RequestHandoffAsync(worldId, second);

        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => coordinator.CompleteHandoffAsync(
                worldId,
                second,
                committedRevision));

        Assert.Equal(1, transfer.EnsureCount);
        Assert.Equal(0, lobby.TransferOwnershipCount);
        var canonical = storage.Worlds[worldId];
        Assert.NotNull(canonical.PeerAuthority);
        Assert.Equal((ulong)1, canonical.PeerAuthority.Generation);
        Assert.Equal(first.ExternalId, canonical.PeerAuthority.Holder.ExternalId);
        var current = await lobby.GetAsync(worldId);
        Assert.NotNull(current);
        Assert.False(current.OwnerConfirmed);
        Assert.Equal(third.ExternalId, current.Owner.ExternalId);
    }

    [Fact]
    public async Task AutomaticPlatformOwnerChange_IsRecoveryPendingAndCannotOverridePersistentHolder()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var storage = new InMemoryWorldStorage();
        var worldId = WorldId.New();
        var first = new UserIdentity("steam", "first");
        var second = new UserIdentity("steam", "second");
        SeedWorld(storage, worldId, first, [first, second]);
        var firstCoordinator = Coordinator(lobby, storage, first);
        var secondCoordinator = Coordinator(lobby, storage, second);

        await firstCoordinator.AcquireHostAsync(worldId, first);
        lobby.SimulateAutomaticOwnerChange(worldId, second);

        var observed = await secondCoordinator.GetSessionAsync(worldId);
        Assert.Equal(SessionState.RecoveryPending, observed.State);
        Assert.Equal(second.ExternalId, observed.ActiveHost?.ExternalId);
        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => secondCoordinator.AcquireHostAsync(worldId, second));
        Assert.Equal(1, lobby.CreateOrGetCount);
    }

    [Fact]
    public async Task ReleaseHost_ClosesLobbyButKeepsPersistentHolderForNextSession()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var storage = new InMemoryWorldStorage();
        var worldId = WorldId.New();
        var host = new UserIdentity("steam", "host");
        SeedWorld(storage, worldId, host, [host]);
        var coordinator = Coordinator(lobby, storage, host);

        await coordinator.AcquireHostAsync(worldId, host);
        await coordinator.ReleaseHostAsync(worldId, host);

        var inactive = await coordinator.GetSessionAsync(worldId);
        Assert.Equal(SessionState.Available, inactive.State);
        Assert.Null(inactive.ActiveHost);
        var canonical = storage.Worlds[worldId];
        Assert.NotNull(canonical.PeerAuthority);
        Assert.Equal((ulong)1, canonical.PeerAuthority.Generation);
        Assert.Equal(host.ExternalId, canonical.PeerAuthority.Holder.ExternalId);

        var hostedAgain = await coordinator.AcquireHostAsync(worldId, host);
        Assert.Equal(SessionState.Hosting, hostedAgain.State);
    }

    [Fact]
    public async Task NonOwner_CannotRequestHandoff()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var storage = new InMemoryWorldStorage();
        var worldId = WorldId.New();
        var host = new UserIdentity("steam", "host");
        var other = new UserIdentity("steam", "other");
        var next = new UserIdentity("steam", "next");
        SeedWorld(storage, worldId, host, [host, other, next]);
        var hostCoordinator = Coordinator(lobby, storage, host);
        var otherCoordinator = Coordinator(lobby, storage, other);

        await hostCoordinator.AcquireHostAsync(worldId, host);

        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => otherCoordinator.RequestHandoffAsync(worldId, next));
    }

    private static PeerWorldSessionCoordinator Coordinator(
        IPeerWorldLobby lobby,
        IWorldStorage storage,
        UserIdentity localUser)
        => new(
            lobby,
            localUser,
            new RecordingRevisionTransfer(),
            storage);

    private static void SeedWorld(
        InMemoryWorldStorage storage,
        WorldId worldId,
        UserIdentity holder,
        IReadOnlyList<UserIdentity> members,
        RevisionId? stateRevision = null)
    {
        storage.Worlds[worldId] = new World(
            worldId,
            "Peer World",
            "fake",
            members,
            RevisionId.New(),
            stateRevision ?? RevisionId.New())
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = new WorldPeerAuthority(holder, 1)
        };
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

    private sealed class InMemoryWorldStorage : IWorldStorage
    {
        public Dictionary<WorldId, World> Worlds { get; } = [];

        public Task SaveWorldAsync(
            World world,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Worlds[world.Id] = world;
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

        public Task StoreEnvironmentRevisionAsync(
            EnvironmentRevision revision,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task StoreRevisionAsync(
            StateRevision revision,
            Stream package,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class InMemoryPeerWorldLobby : IPeerWorldLobby
    {
        private readonly object _gate = new();
        private readonly Dictionary<WorldId, PeerWorldLobbySnapshot> _worlds = [];

        public int CreateOrGetCount { get; private set; }
        public int TransferOwnershipCount { get; private set; }
        public bool FailTransfer { get; init; }

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
                CreateOrGetCount++;
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
                    throw new WorldSessionConflictException(
                        worldId,
                        "The requested host changed before transfer.");
                }

                TransferOwnershipCount++;
                if (FailTransfer)
                {
                    throw new IOException("Injected confirmed lobby transfer rejection.");
                }

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

        public void SimulateAutomaticOwnerChange(
            WorldId worldId,
            UserIdentity newOwner)
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

        private PeerWorldLobbySnapshot RequireOwned(
            WorldId worldId,
            UserIdentity expectedOwner)
        {
            if (!_worlds.TryGetValue(worldId, out var current) ||
                !SameUser(current.Owner, expectedOwner) ||
                !current.OwnerConfirmed)
            {
                throw new WorldSessionConflictException(
                    worldId,
                    "The expected peer-lobby owner is no longer current.");
            }

            return current;
        }

        private static bool SameUser(
            UserIdentity left,
            UserIdentity right)
            => string.Equals(
                   left.Provider,
                   right.Provider,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   left.ExternalId,
                   right.ExternalId,
                   StringComparison.Ordinal);
    }
}
