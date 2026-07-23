using System.Net;
using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardWorldCreationClientTests
{
    [Fact]
    public async Task CreatePostsExistingCanonicalIdsAndBearerToken()
    {
        var world = CreateWorld();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.Created,
            """{ "code": "WorldCreated", "retryable": false }"""));
        using var http = CreateHttpClient(handler);
        var client = new StewardWorldCreationClient(http);

        var status = await client.CreateAsync(world, "access-token");

        Assert.Equal(RemoteWorldCreationStatus.Created, status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://steward.test/api/v1/worlds", request.Uri);
        Assert.Equal("Bearer access-token", request.Authorization);

        using var document = JsonDocument.Parse(request.Body!);
        var root = document.RootElement;
        Assert.Equal(world.Id.Value, root.GetProperty("worldId").GetGuid());
        Assert.Equal(world.GameAdapterId, root.GetProperty("adapterId").GetString());
        Assert.Equal(world.Name, root.GetProperty("displayName").GetString());
        Assert.Equal(world.CurrentStateRevisionId!.Value.Value, root.GetProperty("currentStateRevisionId").GetGuid());
        Assert.Equal(world.CurrentEnvironmentRevisionId!.Value.Value, root.GetProperty("currentEnvironmentRevisionId").GetGuid());
    }

    [Fact]
    public async Task AlreadyExistingIdenticalWorldIsRetryableSuccess()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """{ "code": "WorldAlreadyExists", "retryable": false }"""));
        using var http = CreateHttpClient(handler);
        var client = new StewardWorldCreationClient(http);

        var status = await client.CreateAsync(CreateWorld(), "access-token");

        Assert.Equal(RemoteWorldCreationStatus.AlreadyExists, status);
    }

    [Fact]
    public async Task UnexpectedBackendConflictIsSurfacedWithStableCode()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.Conflict,
            """{ "code": "WorldIdConflict", "retryable": false }"""));
        using var http = CreateHttpClient(handler);
        var client = new StewardWorldCreationClient(http);

        var exception = await Assert.ThrowsAsync<StewardWorldCreationException>(() =>
            client.CreateAsync(CreateWorld(), "access-token"));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal("WorldIdConflict", exception.Code);
        Assert.False(exception.Retryable);
    }

    [Fact]
    public async Task InvalidTokenIsRejectedBeforeNetworkIo()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Network must not be used."));
        using var http = CreateHttpClient(handler);
        var client = new StewardWorldCreationClient(http);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.CreateAsync(CreateWorld(), "bad token"));

        Assert.Empty(handler.Requests);
    }

    private static World CreateWorld()
        => new(
            WorldId.New(),
            "Factory",
            "factorio",
            [new UserIdentity("steam", "76561198000000001", "Tester")],
            RevisionId.New(),
            RevisionId.New());

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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RequestSnapshot(
                request.Method,
                request.RequestUri?.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken)));
            return _responseFactory(request);
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        string? Uri,
        string? Authorization,
        string? Body);
}
