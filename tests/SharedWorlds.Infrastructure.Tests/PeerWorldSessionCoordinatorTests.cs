using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Infrastructure.Sessions;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerWorldSessionCoordinatorTests
{
    [Fact]
    public async Task AcquireHost_RequiresCanonicalHolderAndActiveAccountFence_BeforeLobbyCreation()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var storage = new InMemoryWorldStorage();
        var holderFences = new InMemoryAuthorityFenceStore();
        var memberFences = new InMemoryAuthorityFenceStore();
        var worldId = WorldId.New();
        var holder = new UserIdentity("steam", "holder");
        var member = new UserIdentity("steam", "member");
        var stateId = SeedWorld(storage, worldId, holder, [holder, member]);
        SeedFence(holderFences, worldId, holder, 1, stateId, PeerAuthorityFenceState.Active);
        SeedFence(memberFences, worldId, holder, 1, stateId, PeerAuthorityFenceState.Observed);
        var holderCoordinator = Coordinator(lobby, storage, holderFences, holder);
        var memberCoordinator = Coordinator(lobby, storage, memberFences, member);

        var hosted = await holderCoordinator.AcquireHostAsync(worldId, holder);

        Assert.Equal(SessionState.Hosting, hosted.State);
        Assert.Equal(holder, hosted.ActiveHost);
        Assert.Equal(1, lobby.CreateOrGetCount);
        var live = await lobby.GetAsync(worldId);
        Assert.NotNull(live);
        Assert.Equal((ulong)1, live.AuthorityGeneration);
        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => memberCoordinator.AcquireHostAsync(worldId, member));
        Assert.Equal(1, lobby.CreateOrGetCount);
    }

    [Fact]
    public async Task AcquireHost_RejectsLegacyZeroGenerationLobbyEvenWhenOwnerIsConfirmed()
    {
        var lobby = new InMemoryPeerWorldLobby { ForceLegacyZeroGeneration = true };
        var storage = new InMemoryWorldStorage();
        var fences = new InMemoryAuthorityFenceStore();
        var worldId = WorldId.New();
        var holder = new UserIdentity("steam", "holder");
        var stateId = SeedWorld(storage, worldId, holder, [holder]);
        SeedFence(fences, worldId, holder, 1, stateId, PeerAuthorityFenceState.Active);
        var coordinator = Coordinator(lobby, storage, fences, holder);

        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => coordinator.AcquireHostAsync(worldId, holder));

        var live = await lobby.GetAsync(worldId);
        Assert.NotNull(live);
        Assert.True(live.OwnerConfirmed);
        Assert.Equal((ulong)0, live.AuthorityGeneration);
        var session = await coordinator.GetSessionAsync(worldId);
        Assert.Equal(SessionState.RecoveryPending, session.State);
    }

    [Fact]
    public async Task AcquireHost_StaleFormerHolderIsBlockedByNewerObservedAccountFence()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var storage = new InMemoryWorldStorage();
        var fences = new InMemoryAuthorityFenceStore();
        var worldId = WorldId.New();
        var formerHolder = new UserIdentity("steam", "former");
        var currentHolder = new UserIdentity("steam", "current");
        var staleState = SeedWorld(
            storage,
            worldId,
            formerHolder,
            [formerHolder, currentHolder]);

        SeedFence(
            fences,
            worldId,
            currentHolder,
            2,
            RevisionId.New(),
            PeerAuthorityFenceState.Observed);
        var coordinator = Coordinator(
            lobby,
            storage,
            fences,
            formerHolder);

        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => coordinator.AcquireHostAsync(worldId, formerHolder));

        Assert.Equal(0, lobby.CreateOrGetCount);
        Assert.Equal(staleState, storage.Worlds[worldId].CurrentStateRevisionId);
    }

    [Fact]
    public async Task AcquireHost_RejectsMissingFenceAndLegacySharedWorldWithoutPeerAuthority()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var storage = new InMemoryWorldStorage();
        var fences = new InMemoryAuthorityFenceStore();
        var user = new UserIdentity("steam", "owner");
        var fencedWorldId = WorldId.New();
        SeedWorld(storage, fencedWorldId, user, [user]);
        var coordinator = Coordinator(lobby, storage, fences, user);

        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => coordinator.AcquireHostAsync(fencedWorldId, user));

        var legacyWorldId = WorldId.New();
        storage.Worlds[legacyWorldId] = new World(
            legacyWorldId,
            "Legacy Shared World",
            "fake",
            [user],
            RevisionId.New(),
            RevisionId.New())
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = null
        };
        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => coordinator.AcquireHostAsync(legacyWorldId, user));
        Assert.Equal(0, lobby.CreateOrGetCount);
    }

    [Fact]
    public async Task GracefulHandoff_ActivatesTargetFenceThenObservesNewHolderOnSource()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var storage = new InMemoryWorldStorage();
        var sourceFences = new InMemoryAuthorityFenceStore();
        var targetFences = new InMemoryAuthorityFenceStore();
        var worldId = WorldId.New();
        var source = new UserIdentity("steam", "source");
        var target = new UserIdentity("steam", "target");
        var initialState = SeedWorld(storage, worldId, source, [source, target]);
        SeedFence(sourceFences, worldId, source, 1, initialState, PeerAuthorityFenceState.Active);
        SeedFence(targetFences, worldId, source, 1, initialState, PeerAuthorityFenceState.Observed);
        var committedRevision = RevisionId.New();
        var transfer = new RecordingRevisionTransfer
        {
            AfterEnsure = () => SeedFence(
                targetFences,
                worldId,
                target,
                2,
                committedRevision,
                PeerAuthorityFenceState.Active)
        };
        var sourceCoordinator = new PeerWorldSessionCoordinator(
            lobby,
            source,
            transfer,
            storage,
            sourceFences);
        var targetCoordinator = Coordinator(
            lobby,
            storage,
            targetFences,
            target);

        await sourceCoordinator.AcquireHostAsync(worldId, source);
        await sourceCoordinator.RequestHandoffAsync(worldId, target);
        storage.Worlds[worldId] = storage.Worlds[worldId] with
        {
            CurrentStateRevisionId = committedRevision
        };
        SeedFence(
            sourceFences,
            worldId,
            source,
            1,
            committedRevision,
            PeerAuthorityFenceState.Active);

        await sourceCoordinator.CompleteHandoffAsync(
            worldId,
            target,
            committedRevision);

        Assert.Equal(1, transfer.EnsureCount);
        Assert.Equal(1, lobby.TransferOwnershipCount);
        var canonical = storage.Worlds[worldId];
        Assert.NotNull(canonical.PeerAuthority);
        Assert.Equal((ulong)2, canonical.PeerAuthority.Generation);
        Assert.Equal(target.ExternalId, canonical.PeerAuthority.Holder.ExternalId);

        var sourceFence = await sourceFences.LoadAsync(worldId);
        Assert.NotNull(sourceFence);
        Assert.Equal(PeerAuthorityFenceState.Observed, sourceFence.State);
        Assert.Equal((ulong)2, sourceFence.Generation);
        Assert.Equal(target.ExternalId, sourceFence.Holder.ExternalId);
        Assert.Equal(committedRevision, sourceFence.StateRevisionId);

        var targetFence = await targetFences.LoadAsync(worldId);
        Assert.NotNull(targetFence);
        Assert.Equal(PeerAuthorityFenceState.Active, targetFence.State);
        Assert.Equal((ulong)2, targetFence.Generation);

        var live = await lobby.GetAsync(worldId);
        Assert.NotNull(live);
        Assert.Equal((ulong)2, live.AuthorityGeneration);
        var hostedByTarget = await targetCoordinator.GetSessionAsync(worldId);
        Assert.Equal(SessionState.Hosting, hostedByTarget.State);
        Assert.Equal(target.ExternalId, hostedByTarget.ActiveHost?.ExternalId);
    }

    [Fact]
    public async Task Handoff_RevisionTransferFailureLeavesSourceRelinquishingAndCannotRestoreOldHost()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var storage = new InMemoryWorldStorage();
        var sourceFences = new InMemoryAuthorityFenceStore();
        var worldId = WorldId.New();
        var source = new UserIdentity("steam", "source");
        var target = new UserIdentity("steam", "target");
        var committedRevision = SeedWorld(
            storage,
            worldId,
            source,
            [source, target]);
        SeedFence(sourceFences, worldId, source, 1, committedRevision, PeerAuthorityFenceState.Active);
        var transfer = new RecordingRevisionTransfer { Fail = true };
        var coordinator = new PeerWorldSessionCoordinator(
            lobby,
            source,
            transfer,
            storage,
            sourceFences);

        await coordinator.AcquireHostAsync(worldId, source);
        await coordinator.RequestHandoffAsync(worldId, target);

        await Assert.ThrowsAsync<IOException>(
            () => coordinator.CompleteHandoffAsync(
                worldId,
                target,
                committedRevision));

        var world = storage.Worlds[worldId];
        Assert.Equal((ulong)1, world.PeerAuthority!.Generation);
        Assert.Equal(source.ExternalId, world.PeerAuthority.Holder.ExternalId);
        var fence = await sourceFences.LoadAsync(worldId);
        Assert.NotNull(fence);
        Assert.Equal(PeerAuthorityFenceState.Relinquishing, fence.State);
        Assert.Equal((ulong)2, fence.Generation);
        Assert.Equal(target.ExternalId, fence.Holder.ExternalId);
        Assert.Equal(committedRevision, fence.StateRevisionId);
        Assert.Equal(0, lobby.TransferOwnershipCount);

        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => coordinator.AcquireHostAsync(worldId, source));
        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => coordinator.ReleaseHostAsync(worldId, source));
    }

    [Fact]
    public async Task Handoff_SteamTransferFailureKeepsGenerationNPlusOneAndSurfacesRecoveryPending()
    {
        var lobby = new InMemoryPeerWorldLobby { FailTransfer = true };
        var storage = new InMemoryWorldStorage();
        var sourceFences = new InMemoryAuthorityFenceStore();
        var targetFences = new InMemoryAuthorityFenceStore();
        var worldId = WorldId.New();
        var source = new UserIdentity("steam", "source");
        var target = new UserIdentity("steam", "target");
        var committedRevision = SeedWorld(storage, worldId, source, [source, target]);
        SeedFence(sourceFences, worldId, source, 1, committedRevision, PeerAuthorityFenceState.Active);
        SeedFence(targetFences, worldId, source, 1, committedRevision, PeerAuthorityFenceState.Observed);
        var transfer = new RecordingRevisionTransfer
        {
            AfterEnsure = () => SeedFence(
                targetFences,
                worldId,
                target,
                2,
                committedRevision,
                PeerAuthorityFenceState.Active)
        };
        var coordinator = new PeerWorldSessionCoordinator(
            lobby,
            source,
            transfer,
            storage,
            sourceFences);

        await coordinator.AcquireHostAsync(worldId, source);
        await coordinator.RequestHandoffAsync(worldId, target);

        await Assert.ThrowsAsync<IOException>(
            () => coordinator.CompleteHandoffAsync(
                worldId,
                target,
                committedRevision));

        var world = storage.Worlds[worldId];
        Assert.Equal((ulong)2, world.PeerAuthority!.Generation);
        Assert.Equal(target.ExternalId, world.PeerAuthority.Holder.ExternalId);
        var sourceFence = await sourceFences.LoadAsync(worldId);
        Assert.NotNull(sourceFence);
        Assert.Equal(PeerAuthorityFenceState.Observed, sourceFence.State);
        Assert.Equal((ulong)2, sourceFence.Generation);
        Assert.Equal(target.ExternalId, sourceFence.Holder.ExternalId);

        var session = await coordinator.GetSessionAsync(worldId);
        Assert.Equal(SessionState.RecoveryPending, session.State);
        var observedLobby = await lobby.GetAsync(worldId);
        Assert.NotNull(observedLobby);
        Assert.False(observedLobby.OwnerConfirmed);
        Assert.Equal(source.ExternalId, observedLobby.Owner.ExternalId);
        Assert.Equal((ulong)2, observedLobby.AuthorityGeneration);
    }

    [Fact]
    public async Task Handoff_TargetMustAlreadyBeCanonicalMember()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var storage = new InMemoryWorldStorage();
        var fences = new InMemoryAuthorityFenceStore();
        var worldId = WorldId.New();
        var host = new UserIdentity("steam", "host");
        var outsider = new UserIdentity("steam", "outsider");
        var stateId = SeedWorld(storage, worldId, host, [host]);
        SeedFence(fences, worldId, host, 1, stateId, PeerAuthorityFenceState.Active);
        var coordinator = Coordinator(lobby, storage, fences, host);

        await coordinator.AcquireHostAsync(worldId, host);
        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => coordinator.RequestHandoffAsync(worldId, outsider));
        var current = await lobby.GetAsync(worldId);
        Assert.NotNull(current);
        Assert.Null(current.RequestedHost);
    }

    [Fact]
    public async Task Handoff_LiveOwnerChangeAfterRelinquishmentLeavesSourceFencedForRecovery()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var storage = new InMemoryWorldStorage();
        var fences = new InMemoryAuthorityFenceStore();
        var worldId = WorldId.New();
        var source = new UserIdentity("steam", "source");
        var target = new UserIdentity("steam", "target");
        var third = new UserIdentity("steam", "third");
        var committedRevision = SeedWorld(storage, worldId, source, [source, target, third]);
        SeedFence(fences, worldId, source, 1, committedRevision, PeerAuthorityFenceState.Active);
        var transfer = new RecordingRevisionTransfer
        {
            AfterEnsure = () => lobby.SimulateAutomaticOwnerChange(worldId, third)
        };
        var coordinator = new PeerWorldSessionCoordinator(
            lobby,
            source,
            transfer,
            storage,
            fences);

        await coordinator.AcquireHostAsync(worldId, source);
        await coordinator.RequestHandoffAsync(worldId, target);

        await Assert.ThrowsAsync<WorldSessionConflictException>(
            () => coordinator.CompleteHandoffAsync(
                worldId,
                target,
                committedRevision));

        var world = storage.Worlds[worldId];
        Assert.Equal((ulong)1, world.PeerAuthority!.Generation);
        var fence = await fences.LoadAsync(worldId);
        Assert.NotNull(fence);
        Assert.Equal(PeerAuthorityFenceState.Relinquishing, fence.State);
        Assert.Equal((ulong)2, fence.Generation);
        var current = await lobby.GetAsync(worldId);
        Assert.NotNull(current);
        Assert.False(current.OwnerConfirmed);
        Assert.Equal(third.ExternalId, current.Owner.ExternalId);
        Assert.Equal((ulong)1, current.AuthorityGeneration);
    }

    [Fact]
    public async Task ReleaseHost_ClosesLobbyButKeepsActivePersistentFence()
    {
        var lobby = new InMemoryPeerWorldLobby();
        var storage = new InMemoryWorldStorage();
        var fences = new InMemoryAuthorityFenceStore();
        var worldId = WorldId.New();
        var host = new UserIdentity("steam", "host");
        var stateId = SeedWorld(storage, worldId, host, [host]);
        SeedFence(fences, worldId, host, 1, stateId, PeerAuthorityFenceState.Active);
        var coordinator = Coordinator(lobby, storage, fences, host);

        await coordinator.AcquireHostAsync(worldId, host);
        await coordinator.ReleaseHostAsync(worldId, host);

        var inactive = await coordinator.GetSessionAsync(worldId);
        Assert.Equal(SessionState.Available, inactive.State);
        var fence = await fences.LoadAsync(worldId);
        Assert.NotNull(fence);
        Assert.Equal(PeerAuthorityFenceState.Active, fence.State);
        Assert.Equal((ulong)1, fence.Generation);

        var hostedAgain = await coordinator.AcquireHostAsync(worldId, host);
        Assert.Equal(SessionState.Hosting, hostedAgain.State);
    }

    private static PeerWorldSessionCoordinator Coordinator(
        IPeerWorldLobby lobby,
        IWorldStorage storage,
        IPeerAuthorityFenceStore fences,
        UserIdentity localUser)
        => new(
            lobby,
            localUser,
            new RecordingRevisionTransfer(),
            storage,
            fences);

    private static RevisionId SeedWorld(
        InMemoryWorldStorage storage,
        WorldId worldId,
        UserIdentity holder,
        IReadOnlyList<UserIdentity> members)
    {
        var stateRevision = RevisionId.New();
        storage.Worlds[worldId] = new World(
            worldId,
            "Peer World",
            "fake",
            members,
            RevisionId.New(),
            stateRevision)
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = new WorldPeerAuthority(holder, 1)
        };
        return stateRevision;
    }

    private static void SeedFence(
        InMemoryAuthorityFenceStore store,
        WorldId worldId,
        UserIdentity holder,
        ulong generation,
        RevisionId stateRevision,
        PeerAuthorityFenceState state)
        => store.Records[worldId] = new PeerAuthorityFence(
            worldId,
            holder,
            generation,
            stateRevision,
            state,
            DateTimeOffset.UtcNow);

    private sealed class RecordingRevisionTransfer : IPeerWorldRevisionTransfer
    {
        public int EnsureCount { get; private set; }
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
            if (Fail)
            {
                throw new IOException("Injected peer revision transfer failure.");
            }

            AfterEnsure?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryAuthorityFenceStore : IPeerAuthorityFenceStore
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
        public bool ForceLegacyZeroGeneration { get; init; }

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
            ulong authorityGeneration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                CreateOrGetCount++;
                if (_worlds.TryGetValue(worldId, out var existing))
                {
                    RequireGeneration(existing, authorityGeneration, worldId);
                    return Task.FromResult(existing);
                }

                var created = new PeerWorldLobbySnapshot(
                    worldId,
                    proposedOwner,
                    OwnerConfirmed: true,
                    AuthorityGeneration: ForceLegacyZeroGeneration
                        ? 0UL
                        : authorityGeneration,
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
            ulong expectedAuthorityGeneration,
            UserIdentity requestedHost,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var current = RequireOwned(
                    worldId,
                    expectedOwner,
                    expectedAuthorityGeneration);
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
            ulong expectedAuthorityGeneration,
            UserIdentity newOwner,
            ulong newAuthorityGeneration,
            RevisionId committedRevision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var current = RequireOwned(
                    worldId,
                    expectedOwner,
                    expectedAuthorityGeneration);
                if (expectedAuthorityGeneration == ulong.MaxValue ||
                    newAuthorityGeneration != expectedAuthorityGeneration + 1)
                {
                    throw new WorldSessionConflictException(
                        worldId,
                        "The requested authority generation transition is invalid.");
                }

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
                    _worlds[worldId] = current with
                    {
                        OwnerConfirmed = false,
                        AuthorityGeneration = newAuthorityGeneration,
                        RequestedHost = null,
                        LastCommittedRevision = committedRevision,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    throw new IOException("Injected Steam owner-transfer failure.");
                }

                var updated = current with
                {
                    Owner = newOwner,
                    OwnerConfirmed = true,
                    AuthorityGeneration = newAuthorityGeneration,
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
            ulong expectedAuthorityGeneration,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _ = RequireOwned(
                    worldId,
                    expectedOwner,
                    expectedAuthorityGeneration);
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
            UserIdentity expectedOwner,
            ulong expectedAuthorityGeneration)
        {
            if (!_worlds.TryGetValue(worldId, out var current) ||
                !SameUser(current.Owner, expectedOwner) ||
                !current.OwnerConfirmed)
            {
                throw new WorldSessionConflictException(
                    worldId,
                    "The expected peer-lobby owner is no longer current.");
            }

            RequireGeneration(current, expectedAuthorityGeneration, worldId);
            return current;
        }

        private static void RequireGeneration(
            PeerWorldLobbySnapshot current,
            ulong expectedAuthorityGeneration,
            WorldId worldId)
        {
            if (expectedAuthorityGeneration == 0 ||
                current.AuthorityGeneration != expectedAuthorityGeneration)
            {
                throw new WorldSessionConflictException(
                    worldId,
                    "The expected peer-lobby authority generation is no longer current.");
            }
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
