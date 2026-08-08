using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Sessions;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerAuthorityFencedWorldStorageTests
{
    [Fact]
    public async Task ActivePeerCommit_AdvancesFenceBeforePublishingWorldHead()
    {
        var events = new List<string>();
        var inner = new RecordingWorldStorage(events);
        var fences = new ExactActiveFenceStore(events);
        var holder = new UserIdentity("steam", "holder");
        var worldId = WorldId.New();
        var currentState = RevisionId.New();
        var nextState = RevisionId.New();
        var environmentId = RevisionId.New();
        var current = World(worldId, holder, currentState, environmentId);
        inner.Worlds[worldId] = current;
        inner.Revisions[(worldId, nextState)] = Revision(
            worldId,
            holder,
            nextState,
            currentState,
            environmentId);
        fences.Current = Fence(worldId, holder, currentState);
        var storage = new PeerAuthorityFencedWorldStorage(inner, fences, holder);

        await storage.SaveWorldAsync(current with { CurrentStateRevisionId = nextState });

        Assert.Equal(["fence", "world"], events);
        Assert.Equal(nextState, fences.Current!.StateRevisionId);
        Assert.Equal(nextState, inner.Worlds[worldId].CurrentStateRevisionId);
    }

    [Fact]
    public async Task FenceFailure_LeavesCanonicalWorldOnOldHead()
    {
        var events = new List<string>();
        var inner = new RecordingWorldStorage(events);
        var fences = new ExactActiveFenceStore(events) { FailAdvance = true };
        var holder = new UserIdentity("steam", "holder");
        var worldId = WorldId.New();
        var currentState = RevisionId.New();
        var nextState = RevisionId.New();
        var environmentId = RevisionId.New();
        var current = World(worldId, holder, currentState, environmentId);
        inner.Worlds[worldId] = current;
        inner.Revisions[(worldId, nextState)] = Revision(
            worldId,
            holder,
            nextState,
            currentState,
            environmentId);
        fences.Current = Fence(worldId, holder, currentState);
        var storage = new PeerAuthorityFencedWorldStorage(inner, fences, holder);

        await Assert.ThrowsAsync<IOException>(() =>
            storage.SaveWorldAsync(current with { CurrentStateRevisionId = nextState }));

        Assert.Equal(["fence"], events);
        Assert.Equal(currentState, inner.Worlds[worldId].CurrentStateRevisionId);
        Assert.Equal(currentState, fences.Current!.StateRevisionId);
    }

    [Fact]
    public async Task WorldHeadFailure_AfterFenceAdvanceFailsClosedAndExactRetryCompletes()
    {
        var events = new List<string>();
        var inner = new RecordingWorldStorage(events) { FailNextSave = true };
        var fences = new ExactActiveFenceStore(events);
        var holder = new UserIdentity("steam", "holder");
        var worldId = WorldId.New();
        var currentState = RevisionId.New();
        var nextState = RevisionId.New();
        var environmentId = RevisionId.New();
        var current = World(worldId, holder, currentState, environmentId);
        var next = current with { CurrentStateRevisionId = nextState };
        inner.Worlds[worldId] = current;
        inner.Revisions[(worldId, nextState)] = Revision(
            worldId,
            holder,
            nextState,
            currentState,
            environmentId);
        fences.Current = Fence(worldId, holder, currentState);
        var storage = new PeerAuthorityFencedWorldStorage(inner, fences, holder);

        await Assert.ThrowsAsync<IOException>(() => storage.SaveWorldAsync(next));

        Assert.Equal(nextState, fences.Current!.StateRevisionId);
        Assert.Equal(currentState, inner.Worlds[worldId].CurrentStateRevisionId);

        events.Clear();
        await storage.SaveWorldAsync(next);

        Assert.Equal(["fence-retry", "world"], events);
        Assert.Equal(nextState, inner.Worlds[worldId].CurrentStateRevisionId);
    }

    [Fact]
    public async Task StaleExpectedFence_CannotOverwriteNewerSameGenerationRevision()
    {
        var events = new List<string>();
        var inner = new RecordingWorldStorage(events);
        var fences = new ExactActiveFenceStore(events);
        var holder = new UserIdentity("steam", "holder");
        var worldId = WorldId.New();
        var staleState = RevisionId.New();
        var alreadyFencedState = RevisionId.New();
        var proposedState = RevisionId.New();
        var environmentId = RevisionId.New();
        var current = World(worldId, holder, staleState, environmentId);
        inner.Worlds[worldId] = current;
        inner.Revisions[(worldId, proposedState)] = Revision(
            worldId,
            holder,
            proposedState,
            staleState,
            environmentId);
        fences.Current = Fence(worldId, holder, alreadyFencedState);
        var storage = new PeerAuthorityFencedWorldStorage(inner, fences, holder);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            storage.SaveWorldAsync(current with { CurrentStateRevisionId = proposedState }));

        Assert.Equal(staleState, inner.Worlds[worldId].CurrentStateRevisionId);
        Assert.Equal(alreadyFencedState, fences.Current!.StateRevisionId);
    }

    [Fact]
    public async Task StateAdvanceMustAlreadyExistAsExactDirectChild()
    {
        var events = new List<string>();
        var inner = new RecordingWorldStorage(events);
        var fences = new ExactActiveFenceStore(events);
        var holder = new UserIdentity("steam", "holder");
        var worldId = WorldId.New();
        var currentState = RevisionId.New();
        var nextState = RevisionId.New();
        var environmentId = RevisionId.New();
        var current = World(worldId, holder, currentState, environmentId);
        inner.Worlds[worldId] = current;
        fences.Current = Fence(worldId, holder, currentState);
        var storage = new PeerAuthorityFencedWorldStorage(inner, fences, holder);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            storage.SaveWorldAsync(current with { CurrentStateRevisionId = nextState }));

        Assert.Empty(events);
        Assert.Equal(currentState, fences.Current!.StateRevisionId);
        Assert.Equal(currentState, inner.Worlds[worldId].CurrentStateRevisionId);
    }

    [Fact]
    public async Task AuthorityTransferMetadataSave_DoesNotAdvanceFormerHolderActiveRevision()
    {
        var events = new List<string>();
        var inner = new RecordingWorldStorage(events);
        var fences = new ExactActiveFenceStore(events);
        var source = new UserIdentity("steam", "source");
        var target = new UserIdentity("steam", "target");
        var worldId = WorldId.New();
        var state = RevisionId.New();
        var environmentId = RevisionId.New();
        var current = World(worldId, source, state, environmentId);
        inner.Worlds[worldId] = current;
        fences.Current = Fence(worldId, source, state);
        var storage = new PeerAuthorityFencedWorldStorage(inner, fences, source);
        var transferred = current with
        {
            PeerAuthority = new WorldPeerAuthority(target, 2)
        };

        await storage.SaveWorldAsync(transferred);

        Assert.Equal(["world"], events);
        Assert.Equal(state, fences.Current!.StateRevisionId);
        Assert.Equal(target, inner.Worlds[worldId].PeerAuthority!.Holder);
    }

    private static World World(
        WorldId worldId,
        UserIdentity holder,
        RevisionId state,
        RevisionId environment)
        => new(
            worldId,
            "Peer",
            "fake",
            [holder],
            environment,
            state)
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = new WorldPeerAuthority(holder, 1)
        };

    private static StateRevision Revision(
        WorldId worldId,
        UserIdentity holder,
        RevisionId id,
        RevisionId parent,
        RevisionId environment)
        => new(
            id,
            worldId,
            parent,
            DateTimeOffset.UtcNow,
            holder,
            "fake",
            "package",
            environment);

    private static PeerAuthorityFence Fence(
        WorldId worldId,
        UserIdentity holder,
        RevisionId state)
        => new(
            worldId,
            holder,
            1,
            state,
            PeerAuthorityFenceState.Active,
            DateTimeOffset.UtcNow);

    private sealed class ExactActiveFenceStore : IPeerAuthorityActiveRevisionFenceStore
    {
        private readonly List<string> _events;

        public ExactActiveFenceStore(List<string> events)
            => _events = events;

        public PeerAuthorityFence? Current { get; set; }
        public bool FailAdvance { get; set; }

        public Task<PeerAuthorityFence?> LoadAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Current?.WorldId == worldId ? Current : null);

        public Task SaveAsync(
            PeerAuthorityFence fence,
            CancellationToken cancellationToken = default)
        {
            Current = fence;
            return Task.CompletedTask;
        }

        public Task<PeerAuthorityFence> AdvanceActiveRevisionAsync(
            WorldId worldId,
            UserIdentity holder,
            ulong generation,
            RevisionId expectedStateRevisionId,
            RevisionId nextStateRevisionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Current is not null &&
                Current.WorldId == worldId &&
                Current.State == PeerAuthorityFenceState.Active &&
                Current.Generation == generation &&
                Current.StateRevisionId == nextStateRevisionId &&
                SameUser(Current.Holder, holder))
            {
                _events.Add("fence-retry");
                return Task.FromResult(Current);
            }

            _events.Add("fence");
            if (FailAdvance)
            {
                throw new IOException("Injected fence failure.");
            }

            if (Current is null ||
                Current.WorldId != worldId ||
                Current.State != PeerAuthorityFenceState.Active ||
                Current.Generation != generation ||
                Current.StateRevisionId != expectedStateRevisionId ||
                !SameUser(Current.Holder, holder))
            {
                throw new InvalidDataException("Active fence does not match expected revision.");
            }

            Current = Current with
            {
                StateRevisionId = nextStateRevisionId,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            return Task.FromResult(Current);
        }

        private static bool SameUser(UserIdentity left, UserIdentity right)
            => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
    }

    private sealed class RecordingWorldStorage : IWorldStorage
    {
        private readonly List<string> _events;

        public RecordingWorldStorage(List<string> events)
            => _events = events;

        public Dictionary<WorldId, World> Worlds { get; } = [];
        public Dictionary<(WorldId, RevisionId), StateRevision> Revisions { get; } = [];
        public bool FailNextSave { get; set; }

        public Task SaveWorldAsync(
            World world,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _events.Add("world");
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("Injected World-head failure.");
            }

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
        {
            Revisions[(revision.WorldId, revision.Id)] = revision;
            return Task.CompletedTask;
        }

        public Task<StateRevision?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            Revisions.TryGetValue((worldId, revisionId), out var revision);
            return Task.FromResult(revision);
        }

        public Task<Stream> OpenRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<Stream>(new MemoryStream());
    }
}
