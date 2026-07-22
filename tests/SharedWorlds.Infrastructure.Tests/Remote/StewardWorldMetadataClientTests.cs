using System.Net;
using System.Text;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardWorldMetadataClientTests
{
    [Fact]
    public async Task CurrentRevisionMapsCanonicalHeadAndIntegrityMetadata()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            $$"""
            {
              "code": "CurrentRevisionFound",
              "data": {
                "world": {
                  "worldId": "{{worldId.Value:D}}",
                  "adapterId": "factorio",
                  "displayName": "Factory",
                  "currentStateRevisionId": "{{stateId.Value:D}}",
                  "currentEnvironmentRevisionId": "{{environmentId.Value:D}}",
                  "accessManager": {
                    "provider": "steam",
                    "externalId": "76561198000000001"
                  },
                  "createdAt": "2026-07-22T08:00:00Z",
                  "updatedAt": "2026-07-22T09:00:00Z"
                },
                "state": {
                  "revisionId": "{{stateId.Value:D}}",
                  "byteSize": 12345,
                  "sha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                  "requiredEnvironmentRevisionId": "{{environmentId.Value:D}}",
                  "publishedAt": "2026-07-22T09:00:00Z"
                },
                "environment": {
                  "revisionId": "{{environmentId.Value:D}}",
                  "artifactReference": "packages/environment.package",
                  "byteSize": 456,
                  "sha256": "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
                  "publishedAt": "2026-07-22T08:00:00Z"
                }
              },
              "retryable": false
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardWorldMetadataClient(http);

        var current = await client.GetCurrentRevisionAsync(worldId, "access-token");

        Assert.NotNull(current);
        Assert.Equal(worldId, current.World.WorldId);
        Assert.Equal("factorio", current.World.AdapterId);
        Assert.Equal(stateId, current.World.Head.StateRevisionId);
        Assert.Equal(environmentId, current.World.Head.EnvironmentRevisionId);
        Assert.Equal(12345, current.State!.ByteSize);
        Assert.Equal(environmentId, current.State.RequiredEnvironmentRevisionId);
        Assert.Equal("packages/environment.package", current.Environment!.ArtifactReference);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            $"https://steward.test/api/v1/worlds/{worldId.Value:D}/current-revision",
            request.Uri);
        Assert.Equal("Bearer access-token", request.Authorization);
    }

    [Fact]
    public async Task ArbitraryStateRevisionMapsIntegrityMetadata()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            $$"""
            {
              "code": "StateRevisionFound",
              "data": {
                "revisionId": "{{stateId.Value:D}}",
                "byteSize": 2048,
                "sha256": "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC",
                "requiredEnvironmentRevisionId": "{{environmentId.Value:D}}",
                "publishedAt": "2026-07-22T10:00:00Z"
              },
              "retryable": false
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardWorldMetadataClient(http);

        var revision = await client.GetStateRevisionAsync(worldId, stateId, "access-token");

        Assert.NotNull(revision);
        Assert.Equal(stateId, revision.RevisionId);
        Assert.Equal(2048, revision.ByteSize);
        Assert.Equal(environmentId, revision.RequiredEnvironmentRevisionId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            $"https://steward.test/api/v1/worlds/{worldId.Value:D}/revisions/{stateId.Value:D}/state",
            request.Uri);
    }

    [Fact]
    public async Task ArbitraryEnvironmentRevisionMapsArtifactMetadata()
    {
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            $$"""
            {
              "code": "EnvironmentRevisionFound",
              "data": {
                "revisionId": "{{environmentId.Value:D}}",
                "artifactReference": "packages/environment.package",
                "byteSize": 1024,
                "sha256": "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD",
                "publishedAt": "2026-07-22T08:00:00Z"
              },
              "retryable": false
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardWorldMetadataClient(http);

        var revision = await client.GetEnvironmentRevisionAsync(
            worldId,
            environmentId,
            "access-token");

        Assert.NotNull(revision);
        Assert.Equal(environmentId, revision.RevisionId);
        Assert.Equal("packages/environment.package", revision.ArtifactReference);
        Assert.Equal(1024, revision.ByteSize);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            $"https://steward.test/api/v1/worlds/{worldId.Value:D}/revisions/{environmentId.Value:D}/environment",
            request.Uri);
    }

    [Fact]
    public async Task MissingImmutableRevisionReturnsNull()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.NotFound,
            """
            { "code": "RevisionNotFoundOrUnauthorized", "retryable": false }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardWorldMetadataClient(http);

        var state = await client.GetStateRevisionAsync(
            WorldId.New(),
            RevisionId.New(),
            "access-token");

        Assert.Null(state);
    }

    [Fact]
    public async Task ListWorldsMapsAccessibleWorlds()
    {
        var firstWorld = WorldId.New();
        var firstState = RevisionId.New();
        var secondWorld = WorldId.New();
        var secondState = RevisionId.New();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            $$"""
            {
              "code": "WorldsListed",
              "data": [
                {
                  "worldId": "{{firstWorld.Value:D}}",
                  "adapterId": "factorio",
                  "displayName": "First",
                  "currentStateRevisionId": "{{firstState.Value:D}}",
                  "currentEnvironmentRevisionId": null,
                  "accessManager": { "provider": "steam", "externalId": "one" },
                  "createdAt": "2026-07-20T08:00:00Z",
                  "updatedAt": "2026-07-21T08:00:00Z"
                },
                {
                  "worldId": "{{secondWorld.Value:D}}",
                  "adapterId": "palworld",
                  "displayName": "Second",
                  "currentStateRevisionId": "{{secondState.Value:D}}",
                  "currentEnvironmentRevisionId": null,
                  "accessManager": { "provider": "steam", "externalId": "two" },
                  "createdAt": "2026-07-20T08:00:00Z",
                  "updatedAt": "2026-07-22T08:00:00Z"
                }
              ],
              "retryable": false
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardWorldMetadataClient(http);

        var worlds = await client.ListWorldsAsync("access-token");

        Assert.Equal(2, worlds.Count);
        Assert.Equal(firstWorld, worlds[0].WorldId);
        Assert.Equal(secondWorld, worlds[1].WorldId);
        Assert.Equal("palworld", worlds[1].AdapterId);
    }

    [Fact]
    public async Task MissingWorldReturnsNullInsteadOfTreatingItAsTransportFailure()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.NotFound,
            """
            { "code": "WorldNotFound", "retryable": false }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardWorldMetadataClient(http);

        var world = await client.GetWorldAsync(WorldId.New(), "access-token");

        Assert.Null(world);
    }

    [Fact]
    public async Task UnexpectedTransientFailurePreservesRetryability()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.ServiceUnavailable,
            """
            { "code": "InternalFailure", "retryable": true }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardWorldMetadataClient(http);

        var exception = await Assert.ThrowsAsync<StewardRemoteApiException>(() =>
            client.ListWorldsAsync("access-token"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal("InternalFailure", exception.Code);
        Assert.True(exception.Retryable);
    }

    [Fact]
    public async Task AccessTokenWhitespaceIsRejectedBeforeNetworkIo()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Network must not be used."));
        using var http = CreateHttpClient(handler);
        var client = new StewardWorldMetadataClient(http);

        await Assert.ThrowsAsync<ArgumentException>(() => client.ListWorldsAsync("bad token"));

        Assert.Empty(handler.Requests);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
        => new(handler)
        {
            BaseAddress = new Uri("https://steward.test/")
        };

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public List<RequestSnapshot> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RequestSnapshot(
                request.Method,
                request.RequestUri?.AbsoluteUri,
                request.Headers.Authorization?.ToString()));
            return Task.FromResult(_responseFactory(request));
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        string? Uri,
        string? Authorization);
}
