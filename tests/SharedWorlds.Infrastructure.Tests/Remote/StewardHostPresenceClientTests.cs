using System.Net;
using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardHostPresenceClientTests
{
    [Fact]
    public async Task GetMapsOnlyJoinEvidence()
    {
        var worldId = WorldId.New();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """
            {
              "code": "HostPresence",
              "data": {
                "state": "Ready",
                "address": "203.0.113.20",
                "port": 34197,
                "joinToken": "token-1"
              },
              "retryable": false
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardHostPresenceClient(http);

        var presence = await client.GetAsync(worldId, "access-token");

        Assert.NotNull(presence);
        Assert.Equal(StewardRemoteHostPresenceState.Ready, presence.State);
        Assert.Equal("203.0.113.20", presence.Address);
        Assert.Equal(34197, presence.Port);
        Assert.Equal("token-1", presence.JoinToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            $"https://steward.test/api/v1/worlds/{worldId.Value:D}/host-presence",
            request.Uri);
        Assert.Equal("Bearer access-token", request.Authorization);
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task MissingOrStaleVisiblePresenceReturnsNull()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.NotFound,
            """{ "code": "NotFound", "retryable": false }"""));
        using var http = CreateHttpClient(handler);
        var client = new StewardHostPresenceClient(http);

        Assert.Null(await client.GetAsync(WorldId.New(), "access-token"));
    }

    [Fact]
    public async Task PublishSendsExactReservationAndReadyConnectionEvidence()
    {
        var worldId = WorldId.New();
        var sessionId = Guid.NewGuid();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """{ "code": "HostPresencePublished", "retryable": false }"""));
        using var http = CreateHttpClient(handler);
        var client = new StewardHostPresenceClient(http);

        var status = await client.PublishAsync(
            worldId,
            sessionId,
            9,
            StewardRemoteHostPresenceState.Ready,
            "198.51.100.42",
            16261,
            "join-token",
            "access-token");

        Assert.Equal(PublishStewardRemoteHostPresenceStatus.Published, status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("Bearer access-token", request.Authorization);
        Assert.NotNull(request.Body);

        using var json = JsonDocument.Parse(request.Body);
        var root = json.RootElement;
        Assert.Equal(sessionId, root.GetProperty("reservationSessionId").GetGuid());
        Assert.Equal(9, root.GetProperty("reservationGeneration").GetInt64());
        Assert.Equal("Ready", root.GetProperty("state").GetString());
        Assert.Equal("198.51.100.42", root.GetProperty("address").GetString());
        Assert.Equal(16261, root.GetProperty("port").GetInt32());
        Assert.Equal("join-token", root.GetProperty("joinToken").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "ReservationMismatch", PublishStewardRemoteHostPresenceStatus.ReservationMismatch)]
    [InlineData(HttpStatusCode.NotFound, "NotFound", PublishStewardRemoteHostPresenceStatus.NotFoundOrUnauthorized)]
    [InlineData(HttpStatusCode.BadRequest, "InvalidHostPresence", PublishStewardRemoteHostPresenceStatus.InvalidHostPresence)]
    public async Task PublishMapsExpectedRejections(
        HttpStatusCode statusCode,
        string code,
        PublishStewardRemoteHostPresenceStatus expected)
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            statusCode,
            $$"""{ "code": "{{code}}", "retryable": false }"""));
        using var http = CreateHttpClient(handler);
        var client = new StewardHostPresenceClient(http);

        var result = await client.PublishAsync(
            WorldId.New(),
            Guid.NewGuid(),
            1,
            StewardRemoteHostPresenceState.Starting,
            null,
            null,
            null,
            "access-token");

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task ClearUsesReservationScopedEndpoint()
    {
        var worldId = WorldId.New();
        var sessionId = Guid.NewGuid();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """{ "code": "HostPresenceCleared", "retryable": false }"""));
        using var http = CreateHttpClient(handler);
        var client = new StewardHostPresenceClient(http);

        Assert.True(await client.ClearAsync(worldId, sessionId, 12, "access-token"));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal(
            $"https://steward.test/api/v1/worlds/{worldId.Value:D}/host-presence/{sessionId:D}/12",
            request.Uri);
    }

    [Fact]
    public async Task UnknownStateFailsClosed()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """
            {
              "code": "HostPresence",
              "data": {
                "state": "MaybeReady",
                "address": null,
                "port": null,
                "joinToken": null
              },
              "retryable": false
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardHostPresenceClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.GetAsync(WorldId.New(), "access-token"));
    }

    [Fact]
    public async Task InvalidReservationIdentityIsRejectedBeforeNetworkIo()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Network must not be used."));
        using var http = CreateHttpClient(handler);
        var client = new StewardHostPresenceClient(http);

        await Assert.ThrowsAsync<ArgumentException>(() => client.PublishAsync(
            WorldId.New(),
            Guid.Empty,
            1,
            StewardRemoteHostPresenceState.Starting,
            null,
            null,
            null,
            "access-token"));
        Assert.Empty(handler.Requests);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
        => new(handler) { BaseAddress = new Uri("https://steward.test/") };

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
            => _responseFactory = responseFactory;

        public List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RequestSnapshot(
                request.Method,
                request.RequestUri?.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                body));
            return _responseFactory(request);
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        string? Uri,
        string? Authorization,
        string? Body);
}
