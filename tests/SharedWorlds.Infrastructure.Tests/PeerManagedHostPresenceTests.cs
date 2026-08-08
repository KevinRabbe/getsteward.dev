using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Infrastructure.Sessions;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerManagedHostPresenceTests
{
    [Fact]
    public async Task Registry_StartingReadyEndPreservesExactAuthorityTuple()
    {
        var registry = new PeerManagedHostPresenceRegistry();
        var worldId = WorldId.New();
        var holder = new UserIdentity("steam", "holder");
        var endpoint = new ManagedHostEndpoint(34197, "secret");

        var starting = await registry.MarkStartingAsync(worldId, holder, 4);
        Assert.Equal(PeerManagedHostPresenceState.Starting, starting.State);
        Assert.Equal((ulong)4, starting.AuthorityGeneration);
        Assert.Null(starting.Endpoint);

        var ready = await registry.MarkReadyAsync(worldId, holder, 4, endpoint);
        Assert.Equal(PeerManagedHostPresenceState.Ready, ready.State);
        Assert.Equal(endpoint, ready.Endpoint);

        await registry.EndAsync(worldId, holder, 4);
        Assert.Null(await registry.GetAsync(worldId));
    }

    [Fact]
    public async Task Registry_DifferentHolderOrGenerationCannotOverwriteLivePresence()
    {
        var registry = new PeerManagedHostPresenceRegistry();
        var worldId = WorldId.New();
        var holder = new UserIdentity("steam", "holder");
        var other = new UserIdentity("steam", "other");
        await registry.MarkStartingAsync(worldId, holder, 8);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.MarkReadyAsync(
                worldId,
                holder,
                9,
                new ManagedHostEndpoint(1, null)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.MarkReadyAsync(
                worldId,
                other,
                8,
                new ManagedHostEndpoint(1, null)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.EndAsync(worldId, other, 8));

        var current = await registry.GetAsync(worldId);
        Assert.NotNull(current);
        Assert.Equal(holder, current.Holder);
        Assert.Equal((ulong)8, current.AuthorityGeneration);
        Assert.Equal(PeerManagedHostPresenceState.Starting, current.State);
    }

    [Fact]
    public async Task CoordinatorDecorator_PublishesOnlyConfirmedLocalHostingGeneration()
    {
        var worldId = WorldId.New();
        var local = new UserIdentity("steam", "local");
        var storage = new InMemoryWorldStorage();
        storage.Worlds[worldId] = SharedWorld(worldId, local, 7);
        var inner = new RecordingSessionCoordinator
        {
            Session = new WorldSession(
                worldId,
                SessionState.Hosting,
                local,
                DateTimeOffset.UtcNow)
        };
        var registry = new PeerManagedHostPresenceRegistry();
        var coordinator = new PeerManagedHostPresenceSessionCoordinator(
            inner,
            storage,
            registry,
            local);

        await coordinator.MarkHostStartingAsync(worldId);
        var endpoint = new ManagedHostEndpoint(34197, "join-token");
        await coordinator.MarkHostReadyAsync(worldId, endpoint);

        var ready = await registry.GetAsync(worldId);
        Assert.NotNull(ready);
        Assert.Equal(PeerManagedHostPresenceState.Ready, ready.State);
        Assert.Equal((ulong)7, ready.AuthorityGeneration);
        Assert.Equal(local, ready.Holder);
        Assert.Equal(endpoint, ready.Endpoint);
    }

    [Theory]
    [InlineData(SessionState.Available)]
    [InlineData(SessionState.RecoveryPending)]
    [InlineData(SessionState.HandoffRequested)]
    public async Task CoordinatorDecorator_RefusesPresenceOutsideConfirmedHosting(
        SessionState state)
    {
        var worldId = WorldId.New();
        var local = new UserIdentity("steam", "local");
        var storage = new InMemoryWorldStorage();
        storage.Worlds[worldId] = SharedWorld(worldId, local, 3);
        var inner = new RecordingSessionCoordinator
        {
            Session = new WorldSession(
                worldId,
                state,
                state == SessionState.Available ? null : local,
                DateTimeOffset.UtcNow)
        };
        var registry = new PeerManagedHostPresenceRegistry();
        var coordinator = new PeerManagedHostPresenceSessionCoordinator(
            inner,
            storage,
            registry,
            local);

        await Assert.ThrowsAsync<WorldSessionConflictException>(() =>
            coordinator.MarkHostStartingAsync(worldId));

        Assert.Null(await registry.GetAsync(worldId));
    }

    [Fact]
    public async Task CoordinatorDecorator_RefusesWorldGenerationOrHolderMismatch()
    {
        var worldId = WorldId.New();
        var local = new UserIdentity("steam", "local");
        var remote = new UserIdentity("steam", "remote");
        var storage = new InMemoryWorldStorage();
        storage.Worlds[worldId] = SharedWorld(worldId, remote, 5);
        var inner = new RecordingSessionCoordinator
        {
            Session = new WorldSession(
                worldId,
                SessionState.Hosting,
                local,
                DateTimeOffset.UtcNow)
        };
        var registry = new PeerManagedHostPresenceRegistry();
        var coordinator = new PeerManagedHostPresenceSessionCoordinator(
            inner,
            storage,
            registry,
            local);

        await Assert.ThrowsAsync<WorldSessionConflictException>(() =>
            coordinator.MarkHostReadyAsync(
                worldId,
                new ManagedHostEndpoint(34197, "token")));

        Assert.Null(await registry.GetAsync(worldId));
    }

    [Fact]
    public async Task EndPresence_ClearsExactLocalRecordEvenAfterSessionStops()
    {
        var worldId = WorldId.New();
        var local = new UserIdentity("steam", "local");
        var storage = new InMemoryWorldStorage();
        storage.Worlds[worldId] = SharedWorld(worldId, local, 2);
        var inner = new RecordingSessionCoordinator
        {
            Session = new WorldSession(
                worldId,
                SessionState.Hosting,
                local,
                DateTimeOffset.UtcNow)
        };
        var registry = new PeerManagedHostPresenceRegistry();
        var coordinator = new PeerManagedHostPresenceSessionCoordinator(
            inner,
            storage,
            registry,
            local);

        await coordinator.MarkHostStartingAsync(worldId);
        await coordinator.MarkHostReadyAsync(
            worldId,
            new ManagedHostEndpoint(34197, "token"));
        inner.Session = new WorldSession(
            worldId,
            SessionState.Available,
            null,
            DateTimeOffset.UtcNow);

        await coordinator.EndHostPresenceAsync(worldId);

        Assert.Null(await registry.GetAsync(worldId));
    }

    [Fact]
    public async Task AuthorityOperationsDelegateWithoutPresenceSideEffects()
    {
        var worldId = WorldId.New();
        var local = new UserIdentity("steam", "local");
        var target = new UserIdentity("steam", "target");
        var revision = RevisionId.New();
        var storage = new InMemoryWorldStorage();
        storage.Worlds[worldId] = SharedWorld(worldId, local, 1);
        var inner = new RecordingSessionCoordinator
        {
            Session = new WorldSession(
                worldId,
                SessionState.Hosting,
                local,
                DateTimeOffset.UtcNow)
        };
        var registry = new PeerManagedHostPresenceRegistry();
        var coordinator = new PeerManagedHostPresenceSessionCoordinator(
            inner,
            storage,
            registry,
            local);

        _ = await coordinator.AcquireHostAsync(worldId, local);
        await coordinator.RequestHandoffAsync(worldId, target);
        await coordinator.CompleteHandoffAsync(worldId, target, revision);
        await coordinator.ReleaseHostAsync(worldId, local);

        Assert.Equal(1, inner.AcquireCount);
        Assert.Equal(1, inner.RequestCount);
        Assert.Equal(1, inner.CompleteCount);
        Assert.Equal(1, inner.ReleaseCount);
        Assert.Null(await registry.GetAsync(worldId));
    }

    private static World SharedWorld(
        WorldId worldId,
        UserIdentity holder,
        ulong generation)
        => new(
            worldId,
            "Peer World",
            "fake",
            [holder],
            RevisionId.New(),
            RevisionId.New())
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = new WorldPeerAuthority(holder, generation)
        };

    private sealed class RecordingSessionCoordinator : IWorldSessionCoordinator
    {
        public required WorldSession Session { get; set; }
        public int AcquireCount { get; private set; }
        public int RequestCount { get; private set; }
        public int CompleteCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public Task<WorldSession> GetSessionAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Session);

        public Task<WorldSession> AcquireHostAsync(
            WorldId worldId,
            UserIdentity user,
            CancellationToken cancellationToken = default)
        {
            AcquireCount++;
            return Task.FromResult(Session);
        }

        public Task RequestHandoffAsync(
            WorldId worldId,
            UserIdentity requestedHost,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return Task.CompletedTask;
        }

        public Task CompleteHandoffAsync(
            WorldId worldId,
            UserIdentity newHost,
            RevisionId committedRevision,
            CancellationToken cancellationToken = default)
        {
            CompleteCount++;
            return Task.CompletedTask;
        }

        public Task ReleaseHostAsync(
            WorldId worldId,
            UserIdentity user,
            CancellationToken cancellationToken = default)
        {
            ReleaseCount++;
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

        public Task<IReadOnlyList<World>> ListWorldsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<World>>(Worlds.Values.ToList());

        public Task<bool> DeleteWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Worlds.Remove(worldId));

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
