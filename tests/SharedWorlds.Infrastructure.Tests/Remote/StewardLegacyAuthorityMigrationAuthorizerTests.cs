using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardLegacyAuthorityMigrationAuthorizerTests
{
    private static readonly UserIdentity Holder = new(
        "steam",
        "76561198000001001",
        "Holder");
    private static readonly UserIdentity OtherMember = new(
        "steam",
        "76561198000001002",
        "Other Member");

    [Fact]
    public async Task ExistingMatchingRetirementResumesWithoutReacquiringLegacyAuthority()
    {
        using var registry = new StewardWritableReservationRegistry();
        var world = CreateWorld();
        var retirement = new RetirementClient
        {
            Evidence = Evidence(world, Guid.NewGuid(), generation: 7)
        };
        var coordinator = new LegacyCoordinator(registry, world, Holder);
        var authorizer = new StewardLegacyAuthorityMigrationAuthorizer(
            coordinator,
            registry,
            retirement,
            Holder);

        var allowed = await authorizer.CanInitializePeerAuthorityAsync(world, Holder);

        Assert.True(allowed);
        Assert.Equal(0, coordinator.AcquireCalls);
        Assert.Equal(1, retirement.GetCalls);
        Assert.Equal(0, retirement.RetireCalls);
    }

    [Fact]
    public async Task ExistingRetirementForDifferentCanonicalHeadBlocksPeerGenerationOne()
    {
        using var registry = new StewardWritableReservationRegistry();
        var world = CreateWorld();
        var retirement = new RetirementClient
        {
            Evidence = Evidence(world, Guid.NewGuid(), generation: 7) with
            {
                StateRevisionId = RevisionId.New()
            }
        };
        var coordinator = new LegacyCoordinator(registry, world, Holder);
        var authorizer = new StewardLegacyAuthorityMigrationAuthorizer(
            coordinator,
            registry,
            retirement,
            Holder);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authorizer.CanInitializePeerAuthorityAsync(world, Holder));

        Assert.Equal(0, coordinator.AcquireCalls);
        Assert.Equal(0, retirement.RetireCalls);
    }

    [Fact]
    public async Task ExistingRetirementForDifferentMembershipBlocksPeerGenerationOne()
    {
        using var registry = new StewardWritableReservationRegistry();
        var retiredWorld = CreateWorld();
        var localWorld = retiredWorld with { Members = [Holder, OtherMember] };
        var retirement = new RetirementClient
        {
            Evidence = Evidence(retiredWorld, Guid.NewGuid(), generation: 7)
        };
        var coordinator = new LegacyCoordinator(registry, localWorld, Holder);
        var authorizer = new StewardLegacyAuthorityMigrationAuthorizer(
            coordinator,
            registry,
            retirement,
            Holder);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authorizer.CanInitializePeerAuthorityAsync(localWorld, Holder));

        Assert.Equal(0, coordinator.AcquireCalls);
        Assert.Equal(1, retirement.GetCalls);
        Assert.Equal(0, retirement.RetireCalls);
    }

    [Fact]
    public async Task FreshMigrationAcquiresThenRetiresThenReadsBackBeforeAllowingPeerAuthority()
    {
        using var registry = new StewardWritableReservationRegistry();
        var world = CreateWorld();
        var events = new List<string>();
        var retirement = new RetirementClient(events)
        {
            PersistRetirement = true,
            ExpectedWorld = world
        };
        var coordinator = new LegacyCoordinator(registry, world, Holder, events);
        var authorizer = new StewardLegacyAuthorityMigrationAuthorizer(
            coordinator,
            registry,
            retirement,
            Holder);

        var allowed = await authorizer.CanInitializePeerAuthorityAsync(world, Holder);

        Assert.True(allowed);
        Assert.Equal(1, coordinator.AcquireCalls);
        Assert.Equal(2, retirement.GetCalls);
        Assert.Equal(1, retirement.RetireCalls);
        Assert.Null(registry.Get(world.Id));
        Assert.Equal(new[] { "get", "acquire", "retire", "get" }, events);
    }

    [Fact]
    public async Task RetirementWithoutDurableReadBackDoesNotAuthorizeAndKeepsExactLease()
    {
        using var registry = new StewardWritableReservationRegistry();
        var world = CreateWorld();
        var retirement = new RetirementClient
        {
            PersistRetirement = false,
            ExpectedWorld = world
        };
        var coordinator = new LegacyCoordinator(registry, world, Holder);
        var authorizer = new StewardLegacyAuthorityMigrationAuthorizer(
            coordinator,
            registry,
            retirement,
            Holder);

        await Assert.ThrowsAsync<IOException>(() =>
            authorizer.CanInitializePeerAuthorityAsync(world, Holder));

        var lease = Assert.IsType<StewardWritableReservationLease>(registry.Get(world.Id));
        Assert.Equal(retirement.LastRetiredSessionId, lease.SessionId);
        Assert.Equal(retirement.LastRetiredGeneration, lease.Generation);
        Assert.Equal(0, coordinator.ReleaseCalls);
    }

    [Fact]
    public async Task StaleLocalHeadReleasesStillLegacyLeaseWithoutRetiring()
    {
        using var registry = new StewardWritableReservationRegistry();
        var world = CreateWorld();
        var remoteWorld = world with { CurrentStateRevisionId = RevisionId.New() };
        var retirement = new RetirementClient();
        var coordinator = new LegacyCoordinator(registry, remoteWorld, Holder);
        var authorizer = new StewardLegacyAuthorityMigrationAuthorizer(
            coordinator,
            registry,
            retirement,
            Holder);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            authorizer.CanInitializePeerAuthorityAsync(world, Holder));

        Assert.Equal(1, coordinator.AcquireCalls);
        Assert.Equal(1, coordinator.ReleaseCalls);
        Assert.Equal(0, retirement.RetireCalls);
        Assert.Null(registry.Get(world.Id));
    }

    [Fact]
    public async Task DifferentAuthenticatedIdentityCannotInitiateCutover()
    {
        using var registry = new StewardWritableReservationRegistry();
        var world = CreateWorld();
        var retirement = new RetirementClient();
        var coordinator = new LegacyCoordinator(registry, world, Holder);
        var different = new UserIdentity(
            "steam",
            "76561198000001003",
            "Different");
        var authorizer = new StewardLegacyAuthorityMigrationAuthorizer(
            coordinator,
            registry,
            retirement,
            different);

        var allowed = await authorizer.CanInitializePeerAuthorityAsync(world, Holder);

        Assert.False(allowed);
        Assert.Equal(0, coordinator.AcquireCalls);
        Assert.Equal(0, retirement.GetCalls);
    }

    private static World CreateWorld()
        => new World(
            WorldId.New(),
            "Legacy Shared",
            "factorio",
            [Holder],
            RevisionId.New(),
            RevisionId.New())
        {
            SharingMode = WorldSharingMode.Shared
        };

    private static StewardLegacyAuthorityRetirementEvidence Evidence(
        World world,
        Guid sessionId,
        long generation)
        => new(
            world.Id,
            sessionId,
            generation,
            world.CurrentStateRevisionId!.Value,
            world.CurrentEnvironmentRevisionId,
            StableIdentitySetFingerprint.Compute(world.Members),
            new DateTimeOffset(2026, 8, 8, 3, 0, 0, TimeSpan.Zero));

    private sealed class RetirementClient : IStewardLegacyAuthorityRetirementClient
    {
        private readonly List<string>? _events;

        public RetirementClient(List<string>? events = null)
        {
            _events = events;
        }

        public StewardLegacyAuthorityRetirementEvidence? Evidence { get; set; }
        public bool PersistRetirement { get; set; }
        public World? ExpectedWorld { get; set; }
        public int GetCalls { get; private set; }
        public int RetireCalls { get; private set; }
        public Guid LastRetiredSessionId { get; private set; }
        public long LastRetiredGeneration { get; private set; }

        public Task<StewardLegacyAuthorityRetirementEvidence?> GetAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            GetCalls++;
            _events?.Add("get");
            return Task.FromResult(Evidence);
        }

        public Task<StewardLegacyAuthorityRetirementEvidence> RetireAsync(
            WorldId worldId,
            Guid sessionId,
            long generation,
            CancellationToken cancellationToken = default)
        {
            RetireCalls++;
            _events?.Add("retire");
            LastRetiredSessionId = sessionId;
            LastRetiredGeneration = generation;
            var world = Assert.IsType<World>(ExpectedWorld);
            var evidence = StewardLegacyAuthorityMigrationAuthorizerTests.Evidence(
                world,
                sessionId,
                generation);
            if (PersistRetirement)
            {
                Evidence = evidence;
            }

            return Task.FromResult(evidence);
        }
    }

    private sealed class LegacyCoordinator : IWorldSessionCoordinator
    {
        private readonly StewardWritableReservationRegistry _registry;
        private readonly World _remoteWorld;
        private readonly UserIdentity _holder;
        private readonly List<string>? _events;

        public LegacyCoordinator(
            StewardWritableReservationRegistry registry,
            World remoteWorld,
            UserIdentity holder,
            List<string>? events = null)
        {
            _registry = registry;
            _remoteWorld = remoteWorld;
            _holder = holder;
            _events = events;
        }

        public int AcquireCalls { get; private set; }
        public int ReleaseCalls { get; private set; }

        public Task<WorldSession> GetSessionAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WorldSession(
                worldId,
                SessionState.Available,
                null,
                DateTimeOffset.UtcNow));

        public Task<WorldSession> AcquireHostAsync(
            WorldId worldId,
            UserIdentity user,
            CancellationToken cancellationToken = default)
        {
            AcquireCalls++;
            _events?.Add("acquire");
            var lease = new StewardWritableReservationLease(
                worldId,
                Guid.NewGuid(),
                4,
                "device-a",
                new StewardRemoteWorldHead(
                    _remoteWorld.CurrentStateRevisionId!.Value,
                    _remoteWorld.CurrentEnvironmentRevisionId),
                _holder.Provider,
                _holder.ExternalId);
            Assert.True(_registry.TryRegister(lease, new CancellationTokenSource()));
            return Task.FromResult(new WorldSession(
                worldId,
                SessionState.Hosting,
                user,
                DateTimeOffset.UtcNow));
        }

        public Task RequestHandoffAsync(
            WorldId worldId,
            UserIdentity requestedHost,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CompleteHandoffAsync(
            WorldId worldId,
            UserIdentity newHost,
            RevisionId committedRevision,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ReleaseHostAsync(
            WorldId worldId,
            UserIdentity user,
            CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            var lease = _registry.Get(worldId);
            if (lease is not null)
            {
                _registry.TryResolve(worldId, lease.SessionId, lease.Generation);
            }

            return Task.CompletedTask;
        }
    }
}
