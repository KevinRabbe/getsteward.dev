using System.Text;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Infrastructure.Sessions;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class PeerWorldCatchUpTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"steward-peer-catchup-{Guid.NewGuid():N}");

    [Fact]
    public async Task MissingReplicaRequestsBootstrapAndReturnsCurrentHead()
    {
        var fixture = await CreateFixtureAsync();
        var request = new PeerWorldCatchUpRequest(
            fixture.World.Id,
            fixture.World.PeerAuthority!.Generation,
            LocalStateRevisionId: null);

        var result = await fixture.Router.HandleAsync(
            fixture.Target,
            request);

        Assert.Equal(fixture.World.Id, result.WorldId);
        Assert.Equal(fixture.World.PeerAuthority.Generation, result.AuthorityGeneration);
        Assert.Equal(fixture.CurrentState.Id, result.CurrentStateRevisionId);
        Assert.Equal(1, fixture.BootstrapExchange.TransferCount);
        Assert.Equal(0, fixture.ObserverExchange.TransferCount);
    }

    [Fact]
    public async Task CurrentReplicaNeedsNoTransfer()
    {
        var fixture = await CreateFixtureAsync();
        var request = new PeerWorldCatchUpRequest(
            fixture.World.Id,
            fixture.World.PeerAuthority!.Generation,
            fixture.CurrentState.Id);

        var result = await fixture.Router.HandleAsync(
            fixture.Target,
            request);

        Assert.Equal(fixture.CurrentState.Id, result.CurrentStateRevisionId);
        Assert.Equal(0, fixture.BootstrapExchange.TransferCount);
        Assert.Equal(0, fixture.ObserverExchange.TransferCount);
    }

    [Fact]
    public async Task OlderKnownAncestorRequestsObserverSync()
    {
        var fixture = await CreateFixtureAsync();
        var request = new PeerWorldCatchUpRequest(
            fixture.World.Id,
            fixture.World.PeerAuthority!.Generation,
            fixture.BaseState.Id);

        var result = await fixture.Router.HandleAsync(
            fixture.Target,
            request);

        Assert.Equal(fixture.CurrentState.Id, result.CurrentStateRevisionId);
        Assert.Equal(0, fixture.BootstrapExchange.TransferCount);
        Assert.Single(fixture.ObserverExchange.Offers);
        Assert.Equal(fixture.CurrentState.Id, fixture.ObserverExchange.Offers[0].StateRevision.Id);
    }

    [Fact]
    public async Task NonMemberIsRejectedBeforeTransfer()
    {
        var fixture = await CreateFixtureAsync();
        var outsider = new UserIdentity("steam", "9999", "Outsider");
        var request = RequestAtCurrentGeneration(fixture, fixture.BaseState.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Router.HandleAsync(outsider, request));

        Assert.Equal(0, fixture.BootstrapExchange.TransferCount);
        Assert.Equal(0, fixture.ObserverExchange.TransferCount);
    }

    [Fact]
    public async Task StaleGenerationIsRejectedBeforeTransfer()
    {
        var fixture = await CreateFixtureAsync();
        var request = new PeerWorldCatchUpRequest(
            fixture.World.Id,
            checked(fixture.World.PeerAuthority!.Generation + 1),
            fixture.BaseState.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Router.HandleAsync(fixture.Target, request));

        Assert.Equal(0, fixture.BootstrapExchange.TransferCount);
        Assert.Equal(0, fixture.ObserverExchange.TransferCount);
    }

    [Fact]
    public async Task DurableFenceMismatchIsRejectedBeforeTransfer()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Fences.Record = fixture.Fences.Record! with
        {
            StateRevisionId = fixture.BaseState.Id
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Router.HandleAsync(
                fixture.Target,
                RequestAtCurrentGeneration(fixture, fixture.BaseState.Id)));

        Assert.Equal(0, fixture.BootstrapExchange.TransferCount);
        Assert.Equal(0, fixture.ObserverExchange.TransferCount);
    }

    [Fact]
    public async Task UnconfirmedLobbyOrHandoffIsRejectedBeforeTransfer()
    {
        var fixture = await CreateFixtureAsync();
        fixture.Lobby.Snapshot = fixture.Lobby.Snapshot! with
        {
            RequestedHost = fixture.Target
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Router.HandleAsync(
                fixture.Target,
                RequestAtCurrentGeneration(fixture, fixture.BaseState.Id)));

        fixture.Lobby.Snapshot = fixture.Lobby.Snapshot with
        {
            RequestedHost = null,
            OwnerConfirmed = false
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Router.HandleAsync(
                fixture.Target,
                RequestAtCurrentGeneration(fixture, fixture.BaseState.Id)));

        Assert.Equal(0, fixture.BootstrapExchange.TransferCount);
        Assert.Equal(0, fixture.ObserverExchange.TransferCount);
    }

    [Fact]
    public async Task RouterMustBeBoundExactlyOnce()
    {
        var fixture = await CreateFixtureAsync(bindRouter: false);
        var request = RequestAtCurrentGeneration(fixture, fixture.BaseState.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Router.HandleAsync(fixture.Target, request));

        fixture.Router.Bind(fixture.Bootstrap, fixture.ObserverSync);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Router.Bind(fixture.Bootstrap, fixture.ObserverSync));
    }

    private async Task<Fixture> CreateFixtureAsync(bool bindRouter = true)
    {
        Directory.CreateDirectory(_root);
        var storage = new LocalWorldStorage(Path.Combine(_root, Guid.NewGuid().ToString("N")));
        var host = new UserIdentity("steam", "1001", "Host");
        var target = new UserIdentity("steam", "1002", "Target");
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var baseStateId = RevisionId.New();
        var currentStateId = RevisionId.New();
        var manifest = new EnvironmentManifest(
            1,
            "fake",
            "1.0.0",
            [],
            new Dictionary<string, string>());
        var environment = new EnvironmentRevision(
            environmentId,
            worldId,
            null,
            DateTimeOffset.UtcNow,
            host,
            manifest);
        var baseState = new StateRevision(
            baseStateId,
            worldId,
            null,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            host,
            "fake",
            "base-package",
            environmentId);
        var currentState = new StateRevision(
            currentStateId,
            worldId,
            baseStateId,
            DateTimeOffset.UtcNow,
            host,
            "fake",
            "current-package",
            environmentId);
        var world = new World(
            worldId,
            "Catch-up World",
            "fake",
            [host, target],
            environmentId,
            currentStateId)
        {
            SharingMode = WorldSharingMode.Shared,
            PeerAuthority = new WorldPeerAuthority(host, 3)
        };

        await storage.StoreEnvironmentRevisionAsync(environment);
        await StoreStateAsync(storage, baseState, "base-state");
        await StoreStateAsync(storage, currentState, "current-state");
        await storage.SaveWorldAsync(world);

        var fences = new TestFenceStore
        {
            Record = new PeerAuthorityFence(
                worldId,
                host,
                world.PeerAuthority.Generation,
                currentStateId,
                PeerAuthorityFenceState.Active,
                DateTimeOffset.UtcNow)
        };
        var lobby = new TestLobby
        {
            Snapshot = new PeerWorldLobbySnapshot(
                worldId,
                host,
                OwnerConfirmed: true,
                AuthorityGeneration: world.PeerAuthority.Generation,
                RequestedHost: null,
                LastCommittedRevision: currentStateId,
                UpdatedAt: DateTimeOffset.UtcNow)
        };
        var bootstrapExchange = new RecordingBootstrapExchange();
        var observerExchange = new RecordingObserverExchange();
        var bootstrap = new PeerWorldBootstrapTransferService(
            storage,
            bootstrapExchange);
        var observerSync = new PeerWorldObserverSyncService(
            storage,
            host,
            fences,
            observerExchange);
        var router = new PeerWorldCatchUpRequestRouter(
            storage,
            lobby,
            fences,
            host);
        if (bindRouter)
        {
            router.Bind(bootstrap, observerSync);
        }

        return new Fixture(
            storage,
            world,
            baseState,
            currentState,
            host,
            target,
            fences,
            lobby,
            bootstrapExchange,
            observerExchange,
            bootstrap,
            observerSync,
            router);
    }

    private static PeerWorldCatchUpRequest RequestAtCurrentGeneration(
        Fixture fixture,
        RevisionId? localRevision)
        => new(
            fixture.World.Id,
            fixture.World.PeerAuthority!.Generation,
            localRevision);

    private static async Task StoreStateAsync(
        LocalWorldStorage storage,
        StateRevision state,
        string text)
    {
        await using var payload = new MemoryStream(
            Encoding.UTF8.GetBytes(text),
            writable: false);
        await storage.StoreRevisionAsync(state, payload);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Fixture(
        LocalWorldStorage Storage,
        World World,
        StateRevision BaseState,
        StateRevision CurrentState,
        UserIdentity Host,
        UserIdentity Target,
        TestFenceStore Fences,
        TestLobby Lobby,
        RecordingBootstrapExchange BootstrapExchange,
        RecordingObserverExchange ObserverExchange,
        PeerWorldBootstrapTransferService Bootstrap,
        PeerWorldObserverSyncService ObserverSync,
        PeerWorldCatchUpRequestRouter Router);

    private sealed class RecordingBootstrapExchange : IPeerWorldBootstrapExchange
    {
        public int TransferCount { get; private set; }

        public async Task<PeerWorldRevisionReceipt> TransferBootstrapAsync(
            UserIdentity targetMember,
            PeerWorldRevisionOffer offer,
            Stream statePayload,
            CancellationToken cancellationToken = default)
        {
            TransferCount++;
            await statePayload.CopyToAsync(Stream.Null, cancellationToken);
            return new PeerWorldRevisionReceipt(
                offer.World.Id,
                offer.StateRevision.Id,
                offer.PayloadLength,
                offer.PayloadSha256);
        }
    }

    private sealed class RecordingObserverExchange : IPeerWorldObserverSyncExchange
    {
        public List<PeerWorldRevisionOffer> Offers { get; } = [];
        public int TransferCount => Offers.Count;

        public async Task<PeerWorldRevisionReceipt> TransferObserverRevisionAsync(
            UserIdentity targetMember,
            PeerWorldRevisionOffer offer,
            Stream statePayload,
            CancellationToken cancellationToken = default)
        {
            Offers.Add(offer);
            await statePayload.CopyToAsync(Stream.Null, cancellationToken);
            return new PeerWorldRevisionReceipt(
                offer.World.Id,
                offer.StateRevision.Id,
                offer.PayloadLength,
                offer.PayloadSha256);
        }
    }

    private sealed class TestFenceStore : IPeerAuthorityFenceStore
    {
        public PeerAuthorityFence? Record { get; set; }

        public Task<PeerAuthorityFence?> LoadAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                Record?.WorldId == worldId
                    ? Record
                    : null);
        }

        public Task SaveAsync(
            PeerAuthorityFence fence,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(fence);
            cancellationToken.ThrowIfCancellationRequested();
            Record = fence;
            return Task.CompletedTask;
        }
    }

    private sealed class TestLobby : IPeerWorldLobby
    {
        public PeerWorldLobbySnapshot? Snapshot { get; set; }

        public Task<PeerWorldLobbySnapshot?> GetAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                Snapshot?.WorldId == worldId
                    ? Snapshot
                    : null);
        }

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
}
