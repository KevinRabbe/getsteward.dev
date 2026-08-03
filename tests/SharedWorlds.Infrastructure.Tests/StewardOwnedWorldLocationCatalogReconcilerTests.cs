using System.Net;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Remote;
using SharedWorlds.Infrastructure.Storage;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class StewardOwnedWorldLocationCatalogReconcilerTests : IDisposable
{
    private const string InstallationId = "desktop-installation-a";
    private const string AdapterId = "test.adapter";

    private static readonly DateTimeOffset ObservedAt =
        new(2026, 8, 3, 20, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-owned-location-catalog-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExactLocalCanonicalHeadBecomesDesiredWithoutNetworkAccess()
    {
        var storage = CreateStorage();
        var journal = CreateJournal();
        var fixture = await StoreCanonicalWorldAsync(storage);
        var requestCount = 0;
        using var http = CreateNoNetworkHttp(() => requestCount++);
        var reconciler = CreateReconciler(storage, journal, http);

        await reconciler.ReconcileAsync();

        var state = Assert.IsType<OwnedWorldLocationPublicationState>(
            await journal.LoadAsync(fixture.World.Id));
        Assert.Equal(InstallationId, state.InstallationId);
        Assert.Equal(fixture.State.Id, state.DesiredStateRevisionId);
        Assert.Equal(fixture.Environment.Id, state.DesiredEnvironmentRevisionId);
        Assert.Null(state.ConfirmedStateRevisionId);
        Assert.Null(state.InFlight);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task MissingCanonicalPayloadRecordsExactRemovalRequest()
    {
        var storage = CreateStorage();
        var journal = CreateJournal();
        var fixture = await StoreCanonicalWorldAsync(storage);
        await SeedConfirmedAsync(
            journal,
            fixture.World.Id,
            fixture.State.Id,
            fixture.Environment.Id);
        Assert.True(await storage.EvictRevisionPayloadAsync(
            fixture.World.Id,
            fixture.State.Id));
        var requestCount = 0;
        using var http = CreateNoNetworkHttp(() => requestCount++);
        var reconciler = CreateReconciler(storage, journal, http);

        await reconciler.ReconcileAsync();

        var state = Assert.IsType<OwnedWorldLocationPublicationState>(
            await journal.LoadAsync(fixture.World.Id));
        Assert.True(state.RemovalRequested);
        Assert.Equal(fixture.State.Id, state.ConfirmedStateRevisionId);
        Assert.Equal(fixture.Environment.Id, state.ConfirmedEnvironmentRevisionId);
        Assert.Null(state.InFlight);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task SharedLocalShadowCannotRemainPrivateLocationAuthority()
    {
        var storage = CreateStorage();
        var journal = CreateJournal();
        var fixture = await StoreCanonicalWorldAsync(
            storage,
            WorldSharingMode.Shared);
        await SeedConfirmedAsync(
            journal,
            fixture.World.Id,
            fixture.State.Id,
            fixture.Environment.Id);
        var requestCount = 0;
        using var http = CreateNoNetworkHttp(() => requestCount++);
        var reconciler = CreateReconciler(storage, journal, http);

        await reconciler.ReconcileAsync();

        var state = Assert.IsType<OwnedWorldLocationPublicationState>(
            await journal.LoadAsync(fixture.World.Id));
        Assert.True(state.RemovalRequested);
        Assert.Equal(fixture.State.Id, state.ConfirmedStateRevisionId);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task LocallyDeletedWorldRecordsRemovalWithoutDiscardingEvidence()
    {
        var storage = CreateStorage();
        var journal = CreateJournal();
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        await SeedConfirmedAsync(journal, worldId, stateId, environmentId);
        var requestCount = 0;
        using var http = CreateNoNetworkHttp(() => requestCount++);
        var reconciler = CreateReconciler(storage, journal, http);

        await reconciler.ReconcileAsync();

        var state = Assert.IsType<OwnedWorldLocationPublicationState>(
            await journal.LoadAsync(worldId));
        Assert.True(state.RemovalRequested);
        Assert.Equal(stateId, state.ConfirmedStateRevisionId);
        Assert.Equal(environmentId, state.ConfirmedEnvironmentRevisionId);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task MismatchedStateEnvironmentAssociationFailsClosedAndPreservesJournal()
    {
        var storage = CreateStorage();
        var journal = CreateJournal();
        var fixture = await StoreCanonicalWorldAsync(
            storage,
            linkedEnvironmentId: RevisionId.New());
        await SeedConfirmedAsync(
            journal,
            fixture.World.Id,
            fixture.State.Id,
            fixture.Environment.Id);
        var before = Assert.IsType<OwnedWorldLocationPublicationState>(
            await journal.LoadAsync(fixture.World.Id));
        var requestCount = 0;
        using var http = CreateNoNetworkHttp(() => requestCount++);
        var reconciler = CreateReconciler(storage, journal, http);

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => reconciler.ReconcileAsync());

        Assert.Contains(
            "is not bound to canonical environment revision",
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(before, await journal.LoadAsync(fixture.World.Id));
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task OneMalformedWorldDoesNotBlockIndependentValidWorld()
    {
        var storage = CreateStorage();
        var journal = CreateJournal();
        var valid = await StoreCanonicalWorldAsync(storage);
        var malformedWorld = new World(
            WorldId.New(),
            "Malformed World",
            AdapterId,
            Array.Empty<UserIdentity>(),
            CurrentEnvironmentRevisionId: null,
            CurrentStateRevisionId: RevisionId.New());
        await storage.SaveWorldAsync(malformedWorld);
        var requestCount = 0;
        using var http = CreateNoNetworkHttp(() => requestCount++);
        var reconciler = CreateReconciler(storage, journal, http);

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => reconciler.ReconcileAsync());

        Assert.Contains(
            "only one side of its canonical state/environment head",
            exception.ToString(),
            StringComparison.Ordinal);
        var validState = Assert.IsType<OwnedWorldLocationPublicationState>(
            await journal.LoadAsync(valid.World.Id));
        Assert.Equal(valid.State.Id, validState.DesiredStateRevisionId);
        Assert.Equal(valid.Environment.Id, validState.DesiredEnvironmentRevisionId);
        Assert.Null(await journal.LoadAsync(malformedWorld.Id));
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task AdapterDisagreementFailsClosedWithoutChangingPriorClaim()
    {
        var storage = CreateStorage();
        var journal = CreateJournal();
        var fixture = await StoreCanonicalWorldAsync(
            storage,
            stateAdapterId: "different.adapter");
        await SeedConfirmedAsync(
            journal,
            fixture.World.Id,
            fixture.State.Id,
            fixture.Environment.Id);
        var before = Assert.IsType<OwnedWorldLocationPublicationState>(
            await journal.LoadAsync(fixture.World.Id));
        using var http = CreateNoNetworkHttp(
            () => throw new InvalidOperationException("Network must not be used."));
        var reconciler = CreateReconciler(storage, journal, http);

        var exception = await Assert.ThrowsAsync<AggregateException>(
            () => reconciler.ReconcileAsync());

        Assert.Contains(
            "do not agree on one exact adapter ID",
            exception.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(before, await journal.LoadAsync(fixture.World.Id));
    }

    private LocalWorldStorage CreateStorage()
        => new(Path.Combine(_root, "world-storage"));

    private LocalOwnedWorldLocationPublicationJournal CreateJournal()
        => new(Path.Combine(_root, "publication-state"));

    private static StewardOwnedWorldLocationCatalogReconciler CreateReconciler(
        LocalWorldStorage storage,
        LocalOwnedWorldLocationPublicationJournal journal,
        HttpClient http)
    {
        var publication = new StewardOwnedWorldLocationPublicationService(
            journal,
            new StewardOwnedWorldLocationClient(
                http,
                _ => Task.FromResult<string?>("access-token")),
            InstallationId,
            () => ObservedAt);
        return new(storage, journal, publication);
    }

    private static async Task<CanonicalWorldFixture> StoreCanonicalWorldAsync(
        LocalWorldStorage storage,
        WorldSharingMode sharingMode = WorldSharingMode.LocalOnly,
        RevisionId? linkedEnvironmentId = null,
        string? stateAdapterId = null,
        string? environmentAdapterId = null)
    {
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();
        var environment = new EnvironmentRevision(
            environmentId,
            worldId,
            ParentRevisionId: null,
            ObservedAt,
            CreatedBy: null,
            new EnvironmentManifest(
                SchemaVersion: 1,
                AdapterId: environmentAdapterId ?? AdapterId,
                GameVersion: "1.0.0",
                Components: Array.Empty<EnvironmentComponent>(),
                Configuration: new Dictionary<string, string>()));
        var state = new StateRevision(
            stateId,
            worldId,
            ParentRevisionId: null,
            ObservedAt,
            CreatedBy: null,
            AdapterId: stateAdapterId ?? AdapterId,
            StatePackageId: $"state:{stateId}",
            EnvironmentRevisionId: linkedEnvironmentId ?? environmentId);
        var world = new World(
            worldId,
            "Canonical World",
            AdapterId,
            Array.Empty<UserIdentity>(),
            environmentId,
            stateId)
        {
            SharingMode = sharingMode
        };

        await storage.StoreEnvironmentRevisionAsync(environment);
        await using (var payload = new MemoryStream(new byte[] { 1, 2, 3, 4 }, writable: false))
        {
            await storage.StoreRevisionAsync(state, payload);
        }

        await storage.SaveWorldAsync(world);
        return new(world, state, environment);
    }

    private static Task SeedConfirmedAsync(
        IOwnedWorldLocationPublicationJournal journal,
        WorldId worldId,
        RevisionId stateId,
        RevisionId environmentId)
        => journal.SaveAsync(new OwnedWorldLocationPublicationState(
            worldId,
            InstallationId,
            stateId,
            environmentId,
            stateId,
            environmentId,
            InFlight: null,
            ObservedAt));

    private static HttpClient CreateNoNetworkHttp(Action onRequest)
        => new(new NoNetworkHandler(onRequest))
        {
            BaseAddress = new Uri("https://safe.example/")
        };

    private sealed record CanonicalWorldFixture(
        World World,
        StateRevision State,
        EnvironmentRevision Environment);

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        private readonly Action _onRequest;

        public NoNetworkHandler(Action onRequest)
        {
            _onRequest = onRequest;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _onRequest();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
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
            // Test cleanup must not hide the actual assertion result.
        }
        catch (UnauthorizedAccessException)
        {
            // Test cleanup must not hide the actual assertion result.
        }
    }
}
