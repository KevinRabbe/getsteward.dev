using System.Net;
using System.Text;
using System.Text.Json;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardFriendsBuildSessionClientTests
{
    [Fact]
    public async Task AuthenticateFriendsBuildSendsPrivateCredentialAndMapsNormalSessionTokens()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """
            {
              "code": "Authenticated",
              "data": {
                "accessToken": "access-1",
                "accessExpiresAt": "2026-07-26T12:15:00Z",
                "refreshToken": "refresh-1",
                "refreshExpiresAt": "2026-08-25T12:00:00Z"
              },
              "retryable": false
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardSessionClient(http);

        var result = await client.AuthenticateFriendsBuildAsync(
            "st_friend_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopq",
            "device-a");

        Assert.Equal(RemoteFriendsBuildAuthenticationStatus.Authenticated, result.Status);
        Assert.NotNull(result.Tokens);
        Assert.Equal("access-1", result.Tokens.AccessToken);
        Assert.Equal("refresh-1", result.Tokens.RefreshToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://steward.test/api/v1/auth/friends/session", request.Uri);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(
            "st_friend_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopq",
            body.RootElement.GetProperty("credential").GetString());
        Assert.Equal("device-a", body.RootElement.GetProperty("installationId").GetString());
    }

    [Fact]
    public async Task InvalidFriendsCredentialIsDomainResult()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.Unauthorized,
            """
            { "code": "InvalidFriendsCredential", "retryable": false }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardSessionClient(http);

        var result = await client.AuthenticateFriendsBuildAsync(
            "st_friend_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopq",
            "device-a");

        Assert.Equal(RemoteFriendsBuildAuthenticationStatus.InvalidCredential, result.Status);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task UnconfiguredFriendsBuildProviderPreservesNonRetryableFailure()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.ServiceUnavailable,
            """
            { "code": "IdentityProviderUnavailable", "retryable": false }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardSessionClient(http);

        var exception = await Assert.ThrowsAsync<StewardRemoteApiException>(() =>
            client.AuthenticateFriendsBuildAsync(
                "st_friend_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopq",
                "device-a"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal("IdentityProviderUnavailable", exception.Code);
        Assert.False(exception.Retryable);
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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RequestSnapshot(request.RequestUri?.AbsoluteUri, body));
            return _responseFactory(request);
        }
    }

    private sealed record RequestSnapshot(
        string? Uri,
        string? Body);
}
