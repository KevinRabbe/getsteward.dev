using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.Tests.Worlds;

public sealed class SharedRevisionMetadataServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 21, 23, 0, 0, TimeSpan.Zero);

    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public async Task VerifiedEnvironmentThenStateMetadataCanBeRecordedAndReadByActiveMember()
    {
        var fixture = await Fixture.CreateAsync();
        var environment = fixture.EnvironmentMetadata();
        var state = fixture.StateMetadata(environment.RevisionId);

        Assert.Equal(
            RecordRevisionMetadataStatus.Recorded,
            await fixture.Revisions.RecordVerifiedEnvironmentRevisionAsync(environment));
        Assert.Equal(
            RecordRevisionMetadataStatus.Recorded,
            await fixture.Revisions.RecordVerifiedStateRevisionAsync(state));

        var current = Assert.IsType<SharedCurrentRevisionMetadata>(
            await fixture.Revisions.GetCurrentRevisionMetadataAsync(
                fixture.Manager,
                fixture.World.WorldId));

        Assert.Equal(state, current.State);
        Assert.Equal(environment, current.Environment);
        Assert.Equal(fixture.World.WorldId, current.World.WorldId);
    }

    [Fact]
    public async Task StateMetadataCannotReferenceEnvironmentThatWasNeverRecorded()
    {
        var fixture = await Fixture.CreateAsync();
        var state = fixture.StateMetadata(RevisionId.New());

        var result = await fixture.Revisions.RecordVerifiedStateRevisionAsync(state);

        Assert.Equal(RecordRevisionMetadataStatus.RequiredEnvironmentMissing, result);
        Assert.Null(await fixture.Store.LoadStateRevisionAsync(state.WorldId, state.RevisionId));
    }

    [Fact]
    public async Task SameImmutableMetadataReplayIsIdempotentButConflictingReuseIsRejected()
    {
        var fixture = await Fixture.CreateAsync();
        var environment = fixture.EnvironmentMetadata();
        await fixture.Revisions.RecordVerifiedEnvironmentRevisionAsync(environment);
        var state = fixture.StateMetadata(environment.RevisionId);

        var first = await fixture.Revisions.RecordVerifiedStateRevisionAsync(state);
        var replay = await fixture.Revisions.RecordVerifiedStateRevisionAsync(state);
        var conflict = await fixture.Revisions.RecordVerifiedStateRevisionAsync(
            state with { Sha256 = HashB });

        Assert.Equal(RecordRevisionMetadataStatus.Recorded, first);
        Assert.Equal(RecordRevisionMetadataStatus.AlreadyRecorded, replay);
        Assert.Equal(RecordRevisionMetadataStatus.Conflict, conflict);
        Assert.Equal(state, await fixture.Store.LoadStateRevisionAsync(state.WorldId, state.RevisionId));
    }

    [Fact]
    public async Task RevisionIdsAreScopedByWorldAndDoNotCollideAcrossWorlds()
    {
        var store = new MetadataStore();
        var metadata = new SharedWorldMetadataService(store, () => Now);
        var revisions = new SharedRevisionMetadataService(store, store);
        var firstManager = Steam("76561198000000001");
        var secondManager = Steam("76561198000000002");
        var sharedEnvironmentId = RevisionId.New();
        var sharedStateId = RevisionId.New();
        var firstWorldId = WorldId.New();
        var secondWorldId = WorldId.New();

        await metadata.CreateSharedWorldAsync(
            firstManager,
            new CreateSharedWorldCommand(
                firstWorldId,
                "factorio",
                "First",
                sharedStateId,
                sharedEnvironmentId));
        await metadata.CreateSharedWorldAsync(
            secondManager,
            new CreateSharedWorldCommand(
                secondWorldId,
                "factorio",
                "Second",
                sharedStateId,
                sharedEnvironmentId));

        var firstEnvironment = EnvironmentMetadata(
            firstWorldId,
            sharedEnvironmentId,
            firstManager.Subject,
            "env/first");
        var secondEnvironment = EnvironmentMetadata(
            secondWorldId,
            sharedEnvironmentId,
            secondManager.Subject,
            "env/second");
        await revisions.RecordVerifiedEnvironmentRevisionAsync(firstEnvironment);
        await revisions.RecordVerifiedEnvironmentRevisionAsync(secondEnvironment);

        var firstState = StateMetadata(
            firstWorldId,
            sharedStateId,
            sharedEnvironmentId,
            firstManager.Subject,
            HashA,
            "state/first");
        var secondState = StateMetadata(
            secondWorldId,
            sharedStateId,
            sharedEnvironmentId,
            secondManager.Subject,
            HashB,
            "state/second");

        Assert.Equal(
            RecordRevisionMetadataStatus.Recorded,
            await revisions.RecordVerifiedStateRevisionAsync(firstState));
        Assert.Equal(
            RecordRevisionMetadataStatus.Recorded,
            await revisions.RecordVerifiedStateRevisionAsync(secondState));
        Assert.Equal(
            firstState,
            await revisions.GetStateRevisionMetadataAsync(firstManager, firstWorldId, sharedStateId));
        Assert.Equal(
            secondState,
            await revisions.GetStateRevisionMetadataAsync(secondManager, secondWorldId, sharedStateId));
        Assert.Null(await revisions.GetStateRevisionMetadataAsync(firstManager, secondWorldId, sharedStateId));
    }

    [Fact]
    public async Task RevisionAdapterMustMatchOwningWorld()
    {
        var fixture = await Fixture.CreateAsync();
        var environment = fixture.EnvironmentMetadata() with { AdapterId = "palworld" };
        var state = fixture.StateMetadata(null) with { AdapterId = "palworld" };

        Assert.Equal(
            RecordRevisionMetadataStatus.AdapterMismatch,
            await fixture.Revisions.RecordVerifiedEnvironmentRevisionAsync(environment));
        Assert.Equal(
            RecordRevisionMetadataStatus.AdapterMismatch,
            await fixture.Revisions.RecordVerifiedStateRevisionAsync(state));
    }

    [Fact]
    public async Task NonMemberAndRevocationPendingMemberCannotReadRevisionMetadata()
    {
        var fixture = await Fixture.CreateAsync();
        var environment = fixture.EnvironmentMetadata();
        var state = fixture.StateMetadata(environment.RevisionId);
        await fixture.Revisions.RecordVerifiedEnvironmentRevisionAsync(environment);
        await fixture.Revisions.RecordVerifiedStateRevisionAsync(state);
        var outsider = Steam("76561198000000099");
        var pending = Steam("76561198000000002");
        fixture.Store.AddMember(new SharedWorldMember(
            fixture.World.WorldId,
            pending.Subject,
            SharedWorldMemberStatus.RevocationPending,
            Now));

        Assert.Null(await fixture.Revisions.GetCurrentRevisionMetadataAsync(
            outsider,
            fixture.World.WorldId));
        Assert.Null(await fixture.Revisions.GetStateRevisionMetadataAsync(
            outsider,
            fixture.World.WorldId,
            state.RevisionId));
        Assert.Null(await fixture.Revisions.GetEnvironmentRevisionMetadataAsync(
            pending,
            fixture.World.WorldId,
            environment.RevisionId));
    }

    [Fact]
    public async Task EnvironmentCanUseOpaqueNativeReferenceWithoutHostedPackageMetadata()
    {
        var fixture = await Fixture.CreateAsync();
        var environment = fixture.EnvironmentMetadata() with
        {
            ArtifactReference = "steam-manifest:factorio:stable",
            ByteSize = null,
            Sha256 = null
        };

        var result = await fixture.Revisions.RecordVerifiedEnvironmentRevisionAsync(environment);
        var persisted = await fixture.Revisions.GetEnvironmentRevisionMetadataAsync(
            fixture.Manager,
            fixture.World.WorldId,
            environment.RevisionId);

        Assert.Equal(RecordRevisionMetadataStatus.Recorded, result);
        Assert.Equal(environment, persisted);
    }

    [Fact]
    public async Task InvalidIntegrityMetadataIsRejectedBeforePersistence()
    {
        var fixture = await Fixture.CreateAsync();
        var invalidState = fixture.StateMetadata(null) with { Sha256 = "not-a-hash" };
        var invalidEnvironment = fixture.EnvironmentMetadata() with
        {
            ByteSize = 123,
            Sha256 = null
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Revisions.RecordVerifiedStateRevisionAsync(invalidState));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Revisions.RecordVerifiedEnvironmentRevisionAsync(invalidEnvironment));

        Assert.Empty(fixture.Store.StateRevisions);
        Assert.Empty(fixture.Store.EnvironmentRevisions);
    }

    [Fact]
    public async Task UnknownWorldCannotReceiveRevisionMetadata()
    {
        var fixture = await Fixture.CreateAsync();
        var unknownWorld = WorldId.New();
        var environment = EnvironmentMetadata(
            unknownWorld,
            RevisionId.New(),
            fixture.Manager.Subject,
            "unknown/env");
        var state = StateMetadata(
            unknownWorld,
            RevisionId.New(),
            null,
            fixture.Manager.Subject,
            HashA,
            "unknown/state");

        Assert.Equal(
            RecordRevisionMetadataStatus.WorldNotFound,
            await fixture.Revisions.RecordVerifiedEnvironmentRevisionAsync(environment));
        Assert.Equal(
            RecordRevisionMetadataStatus.WorldNotFound,
            await fixture.Revisions.RecordVerifiedStateRevisionAsync(state));
    }

    private static VerifiedExternalIdentity Steam(string id)
        => new(new ExternalIdentityRef("steam", id));

    private static SharedEnvironmentRevisionMetadata EnvironmentMetadata(
        WorldId worldId,
        RevisionId revisionId,
        ExternalIdentityRef publisher,
        string artifactReference)
        => new(
            worldId,
            revisionId,
            "factorio",
            artifactReference,
            4096,
            HashA,
            publisher,
            Now);

    private static SharedStateRevisionMetadata StateMetadata(
        WorldId worldId,
        RevisionId revisionId,
        RevisionId? environmentRevisionId,
        ExternalIdentityRef publisher,
        string sha256,
        string packageObjectKey)
        => new(
            worldId,
            revisionId,
            "factorio",
            packageObjectKey,
            8192,
            sha256,
            environmentRevisionId,
            publisher,
            Now);

    private sealed class Fixture
    {
        private Fixture(
            MetadataStore store,
            SharedWorldMetadataService metadata,
            SharedRevisionMetadataService revisions,
            VerifiedExternalIdentity manager,
            SharedWorldMetadata world)
        {
            Store = store;
            Metadata = metadata;
            Revisions = revisions;
            Manager = manager;
            World = world;
        }

        public MetadataStore Store { get; }
        public SharedWorldMetadataService Metadata { get; }
        public SharedRevisionMetadataService Revisions { get; }
        public VerifiedExternalIdentity Manager { get; }
        public SharedWorldMetadata World { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var store = new MetadataStore();
            var metadata = new SharedWorldMetadataService(store, () => Now);
            var revisions = new SharedRevisionMetadataService(store, store);
            var manager = Steam("76561198000000001");
            var command = new CreateSharedWorldCommand(
                WorldId.New(),
                "factorio",
                "Factory World",
                RevisionId.New(),
                RevisionId.New());
            var created = await metadata.CreateSharedWorldAsync(manager, command);
            var world = Assert.IsType<SharedWorldMetadata>(created.World);
            return new Fixture(store, metadata, revisions, manager, world);
        }

        public SharedEnvironmentRevisionMetadata EnvironmentMetadata()
            => SharedRevisionMetadataServiceTests.EnvironmentMetadata(
                World.WorldId,
                Assert.IsType<RevisionId>(World.CurrentEnvironmentRevisionId),
                Manager.Subject,
                "environment/current");

        public SharedStateRevisionMetadata StateMetadata(RevisionId? environmentRevisionId)
            => SharedRevisionMetadataServiceTests.StateMetadata(
                World.WorldId,
                World.CurrentStateRevisionId,
                environmentRevisionId,
                Manager.Subject,
                HashA,
                "state/current");
    }

    private sealed class MetadataStore : ISharedWorldMetadataStore, ISharedRevisionMetadataStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<WorldId, SharedWorldMetadata> _worlds = [];
        private readonly Dictionary<(WorldId, ExternalIdentityRef), SharedWorldMember> _members = [];
        private readonly Dictionary<(WorldId, RevisionId), SharedStateRevisionMetadata> _states = [];
        private readonly Dictionary<(WorldId, RevisionId), SharedEnvironmentRevisionMetadata> _environments = [];

        public IReadOnlyDictionary<(WorldId, RevisionId), SharedStateRevisionMetadata> StateRevisions => _states;
        public IReadOnlyDictionary<(WorldId, RevisionId), SharedEnvironmentRevisionMetadata> EnvironmentRevisions => _environments;

        public Task<bool> TryCreateWorldWithManagerAsync(
            SharedWorldMetadata world,
            SharedWorldMember accessManager,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_worlds.ContainsKey(world.WorldId))
                {
                    return Task.FromResult(false);
                }

                _worlds.Add(world.WorldId, world);
                _members.Add((world.WorldId, accessManager.Identity), accessManager);
                return Task.FromResult(true);
            }
        }

        public Task<SharedWorldMetadata?> LoadWorldAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _worlds.TryGetValue(worldId, out var world);
                return Task.FromResult(world);
            }
        }

        public Task<SharedWorldMember?> LoadMemberAsync(
            WorldId worldId,
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _members.TryGetValue((worldId, identity), out var member);
                return Task.FromResult(member);
            }
        }

        public Task<IReadOnlyList<SharedWorldMetadata>> ListWorldsForActiveMemberAsync(
            ExternalIdentityRef identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var worlds = _members.Values
                    .Where(member => member.Identity == identity &&
                                     member.Status == SharedWorldMemberStatus.Active)
                    .Select(member => _worlds[member.WorldId])
                    .ToArray();
                return Task.FromResult<IReadOnlyList<SharedWorldMetadata>>(worlds);
            }
        }

        public Task<StoreRevisionMetadataStatus> TryRecordStateRevisionAsync(
            SharedStateRevisionMetadata revision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var key = (revision.WorldId, revision.RevisionId);
                if (_states.TryGetValue(key, out var existing))
                {
                    return Task.FromResult(existing == revision
                        ? StoreRevisionMetadataStatus.AlreadyRecorded
                        : StoreRevisionMetadataStatus.Conflict);
                }

                _states.Add(key, revision);
                return Task.FromResult(StoreRevisionMetadataStatus.Recorded);
            }
        }

        public Task<StoreRevisionMetadataStatus> TryRecordEnvironmentRevisionAsync(
            SharedEnvironmentRevisionMetadata revision,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                var key = (revision.WorldId, revision.RevisionId);
                if (_environments.TryGetValue(key, out var existing))
                {
                    return Task.FromResult(existing == revision
                        ? StoreRevisionMetadataStatus.AlreadyRecorded
                        : StoreRevisionMetadataStatus.Conflict);
                }

                _environments.Add(key, revision);
                return Task.FromResult(StoreRevisionMetadataStatus.Recorded);
            }
        }

        public Task<SharedStateRevisionMetadata?> LoadStateRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _states.TryGetValue((worldId, revisionId), out var revision);
                return Task.FromResult(revision);
            }
        }

        public Task<SharedEnvironmentRevisionMetadata?> LoadEnvironmentRevisionAsync(
            WorldId worldId,
            RevisionId revisionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _environments.TryGetValue((worldId, revisionId), out var revision);
                return Task.FromResult(revision);
            }
        }

        public void AddMember(SharedWorldMember member)
        {
            lock (_gate)
            {
                _members[(member.WorldId, member.Identity)] = member;
            }
        }
    }
}
