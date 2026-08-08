using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Sessions;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerWorldAuthorityMigrationServiceTests
{
    [Fact]
    public async Task Initialize_WritesActiveFenceBeforePublishingPeerAuthority()
    {
        var storage = new RecordingWorldStorage();
        var fences = new RecordingFenceStore(storage.Events);
        var authorizer = new RecordingAuthorizer(allowed: true);
        var user = new UserIdentity("steam", "1001", "Owner");
        var world = SeedSharedWorld(storage, user);
        var service = new PeerWorldAuthorityMigrationService(
            storage,
            fences,
            authorizer);

        var migrated = await service.InitializeAsync(world.Id, user);

        Assert.NotNull(migrated.PeerAuthority);
        Assert.Equal((ulong)1, migrated.PeerAuthority.Generation);
        Assert.Equal(user.ExternalId, migrated.PeerAuthority.Holder.ExternalId);
        var fence = await fences.LoadAsync(world.Id);
        Assert.NotNull(fence);
        Assert.Equal(PeerAuthorityFenceState.Active, fence.State);
        Assert.Equal((ulong)1, fence.Generation);
        Assert.Equal(world.CurrentStateRevisionId, fence.StateRevisionId);
        Assert.Equal(["fence", "world"], storage.Events);
        Assert.Equal(1, authorizer.CallCount);
    }

    [Fact]
    public async Task Initialize_NeverInfersAuthorityFromMembershipWhenAuthorizerRejects()
    {
        var storage = new RecordingWorldStorage();
        var fences = new RecordingFenceStore(storage.Events);
        var authorizer = new RecordingAuthorizer(allowed: false);
        var user = new UserIdentity("steam", "1001", "Owner");
        var world = SeedSharedWorld(storage, user);
        var service = new PeerWorldAuthorityMigrationService(
            storage,
            fences,
            authorizer);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.InitializeAsync(world.Id, user));

        Assert.Null(storage.Worlds[world.Id].PeerAuthority);
        Assert.Null(await fences.LoadAsync(world.Id));
        Assert.Empty(storage.Events);
    }

    [Fact]
    public async Task Initialize_RejectsNonMemberBeforeExternalAuthorization()
    {
        var storage = new RecordingWorldStorage();
        var fences = new RecordingFenceStore(storage.Events);
        var authorizer = new RecordingAuthorizer(allowed: true);
        var owner = new UserIdentity("steam", "1001", "Owner");
        var outsider = new UserIdentity("steam", "9999", "Outsider");
        var world = SeedSharedWorld(storage, owner);
        var service = new PeerWorldAuthorityMigrationService(
            storage,
            fences,
            authorizer);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.InitializeAsync(world.Id, outsider));

        Assert.Equal(0, authorizer.CallCount);
        Assert.Empty(storage.Events);
    }

    [Fact]
    public async Task Initialize_RetryAfterFenceWriteCanFinishWorldPublication()
    {
        var storage = new RecordingWorldStorage { FailNextWorldSave = true };
        var fences = new RecordingFenceStore(storage.Events);
        var authorizer = new RecordingAuthorizer(allowed: true);
        var user = new UserIdentity("steam", "1001", "Owner");
        var world = SeedSharedWorld(storage, user);
        var service = new PeerWorldAuthorityMigrationService(
            storage,
            fences,
            authorizer);

        await Assert.ThrowsAsync<IOException>(
            () => service.InitializeAsync(world.Id, user));

        Assert.Null(storage.Worlds[world.Id].PeerAuthority);
        var fenceAfterFailure = await fences.LoadAsync(world.Id);
        Assert.NotNull(fenceAfterFailure);
        Assert.Equal(PeerAuthorityFenceState.Active, fenceAfterFailure.State);
        Assert.Equal((ulong)1, fenceAfterFailure.Generation);

        storage.Events.Clear();
        var migrated = await service.InitializeAsync(world.Id, user);

        Assert.NotNull(migrated.PeerAuthority);
        Assert.Equal((ulong)1, migrated.PeerAuthority.Generation);
        Assert.Equal(["world"], storage.Events);
    }

    [Fact]
    public async Task Initialize_IsIdempotentOnlyWhenWorldAndFenceExactlyAgree()
    {
        var storage = new RecordingWorldStorage();
        var fences = new RecordingFenceStore(storage.Events);
        var authorizer = new RecordingAuthorizer(allowed: true);
        var user = new UserIdentity("steam", "1001", "Owner");
        var world = SeedSharedWorld(storage, user);
        var migrated = world with
        {
            PeerAuthority = new WorldPeerAuthority(user, 1)
        };
        storage.Worlds[world.Id] = migrated;
        fences.Records[world.Id] = new PeerAuthorityFence(
            world.Id,
            user,
            1,
            world.CurrentStateRevisionId!.Value,
            PeerAuthorityFenceState.Active,
            DateTimeOffset.UtcNow);
        var service = new PeerWorldAuthorityMigrationService(
            storage,
            fences,
            authorizer);

        var result = await service.InitializeAsync(world.Id, user);

        Assert.Equal(migrated, result);
        Assert.Equal(0, authorizer.CallCount);
        Assert.Empty(storage.Events);
    }

    [Fact]
    public async Task Initialize_RejectsConflictingExistingFence()
    {
        var storage = new RecordingWorldStorage();
        var fences = new RecordingFenceStore(storage.Events);
        var authorizer = new RecordingAuthorizer(allowed: true);
        var user = new UserIdentity("steam", "1001", "Owner");
        var other = new UserIdentity("steam", "1002", "Other");
        var world = SeedSharedWorld(storage, user);
        fences.Records[world.Id] = new PeerAuthorityFence(
            world.Id,
            other,
            2,
            RevisionId.New(),
            PeerAuthorityFenceState.Observed,
            DateTimeOffset.UtcNow);
        var service = new PeerWorldAuthorityMigrationService(
            storage,
            fences,
            authorizer);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.InitializeAsync(world.Id, user));

        Assert.Null(storage.Worlds[world.Id].PeerAuthority);
    }

    private static World SeedSharedWorld(
        RecordingWorldStorage storage,
        UserIdentity owner)
    {
        var world = new World(
            WorldId.New(),
            "Legacy Shared World",
            "fake",
            [owner],
            RevisionId.New(),
            RevisionId.New())
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = null
        };
        storage.Worlds[world.Id] = world;
        return world;
    }

    private sealed class RecordingAuthorizer : IPeerWorldAuthorityMigrationAuthorizer
    {
        private readonly bool _allowed;

        public RecordingAuthorizer(bool allowed)
            => _allowed = allowed;

        public int CallCount { get; private set; }

        public Task<bool> CanInitializePeerAuthorityAsync(
            World world,
            UserIdentity proposedHolder,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(_allowed);
        }
    }

    private sealed class RecordingFenceStore : IPeerAuthorityFenceStore
    {
        private readonly List<string> _events;

        public RecordingFenceStore(List<string> events)
            => _events = events;

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
            _events.Add("fence");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWorldStorage : IWorldStorage
    {
        public Dictionary<WorldId, World> Worlds { get; } = [];
        public List<string> Events { get; } = [];
        public bool FailNextWorldSave { get; set; }

        public Task SaveWorldAsync(
            World world,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailNextWorldSave)
            {
                FailNextWorldSave = false;
                throw new IOException("Injected World save failure.");
            }

            Worlds[world.Id] = world;
            Events.Add("world");
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

        public Task StoreEnvironmentRevisionAsync(EnvironmentRevision revision, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<EnvironmentRevision?> LoadEnvironmentRevisionAsync(WorldId worldId, RevisionId revisionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task StoreRevisionAsync(StateRevision revision, Stream package, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<StateRevision?> LoadStateRevisionAsync(WorldId worldId, RevisionId revisionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<Stream> OpenRevisionAsync(WorldId worldId, RevisionId revisionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
