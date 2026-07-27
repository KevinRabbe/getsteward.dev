using System.Net;
using System.Text;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardWorldPlayerPresenceClientTests
{
    [Fact]
    public async Task GetSnapshotReturnsPlayersAndAuthoritativeHostPresentation()
    {
        var worldId = WorldId.New();
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                $"/api/v1/worlds/{worldId.Value:D}/player-presence",
                request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("access-token", request.Headers.Authorization?.Parameter);

            return JsonResponse(
                """
                {
                  "code":"PlayerPresence",
                  "retryable":false,
                  "data":{
                    "players":[
                      {"provider":"steam","externalId":"76561198000000002"}
                    ],
                    "host":{
                      "provider":"steam",
                      "externalId":"76561198000000001",
                      "state":"Ready"
                    }
                  }
                }
                """);
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://steward.example/")
        };
        var client = new StewardWorldPlayerPresenceClient(
            http,
            new FixedAccessTokenProvider("access-token"));

        var snapshot = await client.GetSnapshotAsync(worldId);

        var player = Assert.Single(snapshot.Players);
        Assert.Equal("steam", player.Provider);
        Assert.Equal("76561198000000002", player.ExternalId);
        Assert.NotNull(snapshot.Host);
        Assert.Equal("steam", snapshot.Host.Provider);
        Assert.Equal("76561198000000001", snapshot.Host.ExternalId);
        Assert.Equal(StewardRemoteHostPresenceState.Ready, snapshot.Host.State);
    }

    [Fact]
    public async Task PublishAndClearCarryNoCallerSuppliedIdentityBody()
    {
        var worldId = WorldId.New();
        var requests = new List<(HttpMethod Method, Uri? Uri, string? Content)>();
        var handler = new RecordingHandler(request =>
        {
            var content = request.Content is null
                ? null
                : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            requests.Add((request.Method, request.RequestUri, content));

            return request.Method == HttpMethod.Put
                ? JsonResponse("{\"code\":\"PlayerPresencePublished\",\"retryable\":false}")
                : JsonResponse("{\"code\":\"PlayerPresenceCleared\",\"retryable\":false}");
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://steward.example/")
        };
        var client = new StewardWorldPlayerPresenceClient(
            http,
            new FixedAccessTokenProvider("access-token"));

        await client.PublishAsync(worldId);
        await client.ClearAsync(worldId);

        Assert.Collection(
            requests,
            publish =>
            {
                Assert.Equal(HttpMethod.Put, publish.Method);
                Assert.Equal($"/api/v1/worlds/{worldId.Value:D}/player-presence", publish.Uri?.AbsolutePath);
                Assert.Null(publish.Content);
            },
            clear =>
            {
                Assert.Equal(HttpMethod.Delete, clear.Method);
                Assert.Equal($"/api/v1/worlds/{worldId.Value:D}/player-presence", clear.Uri?.AbsolutePath);
                Assert.Null(clear.Content);
            });
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class FixedAccessTokenProvider(string token) : IStewardAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(token);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responseFactory(request));
    }
}
