using System.Net;
using System.Text;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardAccessSessionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FreshAccessTokenDoesNotRefresh()
    {
        var handler = new CountingHandler(_ => throw new InvalidOperationException("Refresh must not run."));
        using var http = CreateHttpClient(handler);
        var client = new StewardSessionClient(http);
        using var session = new StewardAccessSession(
            client,
            "device-a",
            Tokens("access-1", Now.AddMinutes(10), "refresh-1", Now.AddDays(10)),
            () => Now);

        var access = await session.GetAccessTokenAsync();

        Assert.Equal("access-1", access);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ConcurrentNearExpiryRequestsRotateRefreshCredentialOnce()
    {
        var handler = new CountingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """
            {
              "code": "Refreshed",
              "data": {
                "accessToken": "access-2",
                "accessExpiresAt": "2026-07-22T11:00:00Z",
                "refreshToken": "refresh-2",
                "refreshExpiresAt": "2026-08-21T11:00:00Z"
              },
              "retryable": false
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardSessionClient(http);
        using var session = new StewardAccessSession(
            client,
            "device-a",
            Tokens("access-1", Now.AddSeconds(30), "refresh-1", Now.AddDays(10)),
            () => Now);

        var accesses = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => session.GetAccessTokenAsync()));

        Assert.All(accesses, access => Assert.Equal("access-2", access));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ExpiredRefreshCredentialEndsSessionWithoutNetworkIo()
    {
        var handler = new CountingHandler(_ => throw new InvalidOperationException("Refresh must not run."));
        using var http = CreateHttpClient(handler);
        var client = new StewardSessionClient(http);
        using var session = new StewardAccessSession(
            client,
            "device-a",
            Tokens("access-1", Now.AddMinutes(-1), "refresh-1", Now.AddSeconds(-1)),
            () => Now);

        await Assert.ThrowsAsync<StewardSessionExpiredException>(() => session.GetAccessTokenAsync());
        await Assert.ThrowsAsync<StewardSessionExpiredException>(() => session.GetAccessTokenAsync());
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task BackendInvalidRefreshCredentialEndsSession()
    {
        var handler = new CountingHandler(_ => JsonResponse(
            HttpStatusCode.Unauthorized,
            """
            { "code": "InvalidRefreshCredential", "retryable": false }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardSessionClient(http);
        using var session = new StewardAccessSession(
            client,
            "device-a",
            Tokens("access-1", Now.AddSeconds(10), "refresh-1", Now.AddDays(1)),
            () => Now);

        await Assert.ThrowsAsync<StewardSessionExpiredException>(() => session.GetAccessTokenAsync());
        await Assert.ThrowsAsync<StewardSessionExpiredException>(() => session.GetAccessTokenAsync());
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RevokeClearsLocalSessionEvenWhenBackendReportsAlreadyRevoked()
    {
        var handler = new CountingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """
            { "code": "NotRevoked", "retryable": false }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardSessionClient(http);
        using var session = new StewardAccessSession(
            client,
            "device-a",
            Tokens("access-1", Now.AddMinutes(10), "refresh-1", Now.AddDays(1)),
            () => Now);

        var revoked = await session.RevokeAsync();

        Assert.False(revoked);
        await Assert.ThrowsAsync<StewardSessionExpiredException>(() => session.GetAccessTokenAsync());
        Assert.Equal(1, handler.RequestCount);
    }

    private static StewardRemoteSessionTokens Tokens(
        string accessToken,
        DateTimeOffset accessExpiresAt,
        string refreshToken,
        DateTimeOffset refreshExpiresAt)
        => new(accessToken, accessExpiresAt, refreshToken, refreshExpiresAt);

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

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;
        private int _requestCount;

        public CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(_responseFactory(request));
        }
    }
}
