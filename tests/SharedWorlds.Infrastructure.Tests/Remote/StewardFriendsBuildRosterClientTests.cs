using System.Net;
using System.Text;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardFriendsBuildRosterClientTests
{
    [Fact]
    public async Task ListsNamedFriendsBuildIdentitiesWithAuthenticatedRequest()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "code": "FriendsBuildIdentitiesFound",
                  "data": [
                    { "provider": "friends-build", "externalId": "friend-0001", "displayName": "Kevin" },
                    { "provider": "friends-build", "externalId": "friend-0002", "displayName": "Alex" }
                  ],
                  "retryable": false
                }
                """,
                Encoding.UTF8,
                "application/json")
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://steward.test/")
        };
        var client = new StewardWorldAccessClient(http, new StaticTokenProvider());

        var identities = await client.ListFriendsBuildIdentitiesAsync();

        Assert.Equal(2, identities.Count);
        Assert.Equal(new StewardRemoteNamedIdentity("friends-build", "friend-0001", "Kevin"), identities[0]);
        Assert.Equal(new StewardRemoteNamedIdentity("friends-build", "friend-0002", "Alex"), identities[1]);
        Assert.Equal("/api/v1/auth/friends/identities", handler.Path);
        Assert.Equal("Bearer access-token", handler.Authorization);
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

        public string? Path { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(_responseFactory(request));
        }
    }
}
