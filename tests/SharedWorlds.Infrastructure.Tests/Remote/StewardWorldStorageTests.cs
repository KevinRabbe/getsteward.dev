using System.Net;
using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardWorldStorageTests
{
    [Fact]
    public async Task SuccessfulCanonicalCommitUsesExactLeaseAndResolvesIt()
    {
        var worldId = WorldId.New();
        var baseState = RevisionId.New();
        var candidateState = RevisionId.New();
        var environmentId = RevisionId.New();
        var sessionId = Guid.NewGuid();
        var authority = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            $$"""
            {
              "code": "Committed",
              "data": {
                "currentHead": {
                  "stateRevisionId": "{{candidateState.Value:D}}",
                  "environmentRevisionId": "{{environmentId.Value:D}}"
                },
                "candidateHead": {
                  "stateRevisionId": "{{candidateState.Value:D}}",
                  "environmentRevisionId": "{{environmentId.Value:D}}"
                }
              },
              "retryable": false
            }
            """));
        using var harness = new StorageHarness(authority);
        RegisterLease(harness.Registry, worldId, sessionId, 7, baseState, environmentId);
        var world = CoreWorld(worldId, candidateState, environmentId);

        await harness.Storage.SaveWorldAsync(world);

        Assert.Null(harness.Registry.Get(worldId));
        var request = Assert.Single(authority.Requests);
        Assert.EndsWith(
            $"/worlds/{worldId.Value:D}/reservation/commit",
            request.Uri,
            StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(request.IdempotencyKey));
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(sessionId, body.RootElement.GetProperty("sessionId").GetGuid());
        Assert.Equal(7, body.RootElement.GetProperty("generation").GetInt64());
        Assert.Equal(baseState.Value, body.RootElement.GetProperty("expectedStateRevisionId").GetGuid());
        Assert.Equal(environmentId.Value, body.RootElement.GetProperty("expectedEnvironmentRevisionId").GetGuid());
        Assert.Equal(candidateState.Value, body.RootElement.GetProperty("candidateStateRevisionId").GetGuid());
        Assert.Equal(environmentId.Value, body.RootElement.GetProperty("candidateEnvironmentRevisionId").GetGuid());
    }

    [Fact]
    public async Task AmbiguousCommitRetriesSameIdempotencyKeyAndPreservesLease()
    {
        var worldId = WorldId.New();
        var baseState = RevisionId.New();
        var candidateState = RevisionId.New();
        var environmentId = RevisionId.New();
        var sessionId = Guid.NewGuid();
        var authority = new RecordingHandler(_ => throw new HttpRequestException("network down"));
        using var harness = new StorageHarness(
            authority,
            new StewardWorldStorageOptions(commitTransportAttempts: 2));
        RegisterLease(harness.Registry, worldId, sessionId, 9, baseState, environmentId);

        var exception = await Assert.ThrowsAsync<StewardCommitOutcomeUnknownException>(() =>
            harness.Storage.SaveWorldAsync(CoreWorld(worldId, candidateState, environmentId)));

        Assert.Equal(worldId, exception.WorldId);
        Assert.Equal(candidateState, exception.CandidateRevisionId);
        var lease = Assert.IsType<StewardWritableReservationLease>(harness.Registry.Get(worldId));
        Assert.Equal(sessionId, lease.SessionId);
        Assert.Equal(9, lease.Generation);
        Assert.Equal(2, authority.Requests.Count);
        Assert.False(string.IsNullOrWhiteSpace(authority.Requests[0].IdempotencyKey));
        Assert.Equal(authority.Requests[0].IdempotencyKey, authority.Requests[1].IdempotencyKey);
        Assert.Equal(authority.Requests[0].Body, authority.Requests[1].Body);
    }

    [Fact]
    public async Task DefinitiveHeadConflictPreservesLeaseAndCandidateResponsibility()
    {
        var worldId = WorldId.New();
        var baseState = RevisionId.New();
        var candidateState = RevisionId.New();
        var otherState = RevisionId.New();
        var environmentId = RevisionId.New();
        var sessionId = Guid.NewGuid();
        var authority = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.Conflict,
            $$"""
            {
              "code": "HeadChanged",
              "data": {
                "currentHead": {
                  "stateRevisionId": "{{otherState.Value:D}}",
                  "environmentRevisionId": "{{environmentId.Value:D}}"
                },
                "candidateHead": {
                  "stateRevisionId": "{{candidateState.Value:D}}",
                  "environmentRevisionId": "{{environmentId.Value:D}}"
                }
              },
              "retryable": false
            }
            """));
        using var harness = new StorageHarness(authority);
        RegisterLease(harness.Registry, worldId, sessionId, 11, baseState, environmentId);

        var exception = await Assert.ThrowsAsync<StewardWorldStorageException>(() =>
            harness.Storage.SaveWorldAsync(CoreWorld(worldId, candidateState, environmentId)));

        Assert.Equal("HeadChanged", exception.Code);
        var lease = Assert.IsType<StewardWritableReservationLease>(harness.Registry.Get(worldId));
        Assert.Equal(sessionId, lease.SessionId);
        Assert.Equal(11, lease.Generation);
    }

    [Fact]
    public async Task EnvironmentManifestReadReconstructsCoreEnvironmentRevision()
    {
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var metadata = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            $$"""
            {
              "code": "EnvironmentRevisionFound",
              "data": {
                "revisionId": "{{environmentId.Value:D}}",
                "artifactReference": "manifest:{{environmentId.Value:N}}",
                "byteSize": null,
                "sha256": null,
                "publishedAt": "2026-07-22T10:00:00Z",
                "manifest": {
                  "schemaVersion": 1,
                  "adapterId": "factorio",
                  "gameVersion": "2.0.0",
                  "components": [
                    {
                      "kind": "mod",
                      "id": "base",
                      "version": "2.0.0",
                      "source": "steam",
                      "metadata": { "required": "true" }
                    }
                  ],
                  "configuration": { "difficulty": "normal" }
                }
              },
              "retryable": false
            }
            """));
        using var harness = new StorageHarness(
            new RecordingHandler(_ => throw new InvalidOperationException("Authority must not be used.")),
            metadataHandler: metadata);

        var revision = await harness.Storage.LoadEnvironmentRevisionAsync(worldId, environmentId);

        Assert.NotNull(revision);
        Assert.Equal(environmentId, revision.Id);
        Assert.Equal(worldId, revision.WorldId);
        Assert.Equal("factorio", revision.Manifest.AdapterId);
        Assert.Equal("2.0.0", revision.Manifest.GameVersion);
        Assert.Equal("normal", revision.Manifest.Configuration["difficulty"]);
        var component = Assert.Single(revision.Manifest.Components);
        Assert.Equal("base", component.Id);
        Assert.Equal("true", component.Metadata!["required"]);
    }

    [Fact]
    public async Task LegacyEnvironmentWithoutManifestFailsClosed()
    {
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var metadata = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            $$"""
            {
              "code": "EnvironmentRevisionFound",
              "data": {
                "revisionId": "{{environmentId.Value:D}}",
                "artifactReference": "native:legacy",
                "byteSize": null,
                "sha256": null,
                "publishedAt": "2026-07-22T10:00:00Z",
                "manifest": null
              },
              "retryable": false
            }
            """));
        using var harness = new StorageHarness(
            new RecordingHandler(_ => throw new InvalidOperationException("Authority must not be used.")),
            metadataHandler: metadata);

        var exception = await Assert.ThrowsAsync<StewardWorldStorageException>(() =>
            harness.Storage.LoadEnvironmentRevisionAsync(worldId, environmentId));

        Assert.Equal("EnvironmentManifestUnavailable", exception.Code);
    }

    private static World CoreWorld(
        WorldId worldId,
        RevisionId stateId,
        RevisionId environmentId)
        => new(
            worldId,
            "Factory",
            "factorio",
            [new UserIdentity("steam", "76561198000000001", "Tester")],
            environmentId,
            stateId)
        {
            SharingMode = WorldSharingMode.Shared
        };

    private static void RegisterLease(
        StewardWritableReservationRegistry registry,
        WorldId worldId,
        Guid sessionId,
        long generation,
        RevisionId stateId,
        RevisionId environmentId)
    {
        var registered = registry.TryRegister(
            new StewardWritableReservationLease(
                worldId,
                sessionId,
                generation,
                "device-a",
                new StewardRemoteWorldHead(stateId, environmentId),
                "steam",
                "76561198000000001"),
            new CancellationTokenSource());
        Assert.True(registered);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StorageHarness : IDisposable
    {
        private readonly string _cacheRoot;
        private readonly List<HttpClient> _clients = [];

        public StorageHarness(
            RecordingHandler authorityHandler,
            StewardWorldStorageOptions? options = null,
            RecordingHandler? metadataHandler = null)
        {
            Registry = new StewardWritableReservationRegistry();
            _cacheRoot = Path.Combine(Path.GetTempPath(), "steward-storage-tests", Guid.NewGuid().ToString("N"));

            var metadataHttp = Track(Client(metadataHandler ?? ThrowingHandler()));
            var authorityHttp = Track(Client(authorityHandler));
            var downloadApiHttp = Track(Client(ThrowingHandler()));
            var uploadApiHttp = Track(Client(ThrowingHandler()));
            var transferHttp = Track(new HttpClient(ThrowingHandler()));

            var metadata = new StewardWorldMetadataClient(metadataHttp);
            var packages = new StewardVerifiedPackageSource(
                new StewardPackageDownloadClient(downloadApiHttp),
                new VerifiedPackageCache(
                    _cacheRoot,
                    transferHttp,
                    new VerifiedPackageCacheOptions(
                        minimumFreeSpaceReserveBytes: 0,
                        copyBufferBytes: 64 * 1024)));
            var uploads = new StewardPackageUploadClient(uploadApiHttp, transferHttp);
            var authority = new StewardAuthorityClient(authorityHttp);
            Storage = new StewardWorldStorage(
                metadata,
                packages,
                uploads,
                authority,
                new StaticTokenProvider(),
                Registry,
                options);
        }

        public StewardWorldStorage Storage { get; }
        public StewardWritableReservationRegistry Registry { get; }

        public void Dispose()
        {
            Registry.Dispose();
            foreach (var client in _clients)
            {
                client.Dispose();
            }

            if (Directory.Exists(_cacheRoot))
            {
                Directory.Delete(_cacheRoot, recursive: true);
            }
        }

        private HttpClient Track(HttpClient client)
        {
            _clients.Add(client);
            return client;
        }

        private static HttpClient Client(HttpMessageHandler handler)
            => new(handler)
            {
                BaseAddress = new Uri("https://steward.test/")
            };

        private static RecordingHandler ThrowingHandler()
            => new(_ => throw new InvalidOperationException("Unexpected network request."));
    }

    private sealed class StaticTokenProvider : IStewardAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult("access-token");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RequestSnapshot(
                request.RequestUri?.AbsoluteUri,
                request.Headers.TryGetValues("Idempotency-Key", out var values)
                    ? values.Single()
                    : null,
                body));
            return _responseFactory(request);
        }
    }

    private sealed record RequestSnapshot(
        string? Uri,
        string? IdempotencyKey,
        string? Body);
}
