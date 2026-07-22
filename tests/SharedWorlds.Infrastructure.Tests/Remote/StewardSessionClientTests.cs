using System.Net;
using System.Text;
using System.Text.Json;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardSessionClientTests
{
    [Fact]
    public async Task AuthenticateSteamSendsTicketAndMapsTokens()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """
            {
              "code": "Authenticated",
              "data": {
                "accessToken": "access-1",
                "accessExpiresAt": "2026-07-22T10:00:00Z",
                "refreshToken": "refresh-1",
                "refreshExpiresAt": "2026-08-21T10:00:00Z"
              },
              "retryable": false
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardSessionClient(http);

        var result = await client.AuthenticateSteamAsync("AABBCC", "device-a");

        Assert.Equal(RemoteSteamAuthenticationStatus.Authenticated, result.Status);
        Assert.NotNull(result.Tokens);
        Assert.Equal("access-1", result.Tokens.AccessToken);
        Assert.Equal("refresh-1", result.Tokens.RefreshToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://steward.test/api/v1/auth/steam/session", request.Uri);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("AABBCC", body.RootElement.GetProperty("ticketHex").GetString());
        Assert.Equal("device-a", body.RootElement.GetProperty("installationId").GetString());
    }

    [Fact]
    public async Task InvalidSteamTicketIsDomainResult()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.Unauthorized,
            """
            { "code": "InvalidSteamTicket", "retryable": false }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardSessionClient(http);

        var result = await client.AuthenticateSteamAsync("BAD", "device-a");

        Assert.Equal(RemoteSteamAuthenticationStatus.InvalidTicket, result.Status);
        Assert.Null(result.Tokens);
    }

    [Fact]
    public async Task RefreshMapsRotatedTokens()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
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

        var result = await client.RefreshAsync("refresh-1", "device-a");

        Assert.Equal(RemoteSessionRefreshStatus.Refreshed, result.Status);
        Assert.Equal("access-2", result.Tokens!.AccessToken);
        Assert.Equal("refresh-2", result.Tokens.RefreshToken);
    }

    [Fact]
    public async Task InvalidRefreshCredentialIsDomainResult()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.Unauthorized,
            """
            { "code": "InvalidRefreshCredential", "retryable": false }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardSessionClient(http);

        var result = await client.RefreshAsync("refresh-1", "device-a");

        Assert.Equal(RemoteSessionRefreshStatus.InvalidCredential, result.Status);
        Assert.Null(result.Tokens);
    }

    [Theory]
    [InlineData("Revoked", true)]
    [InlineData("NotRevoked", false)]
    public async Task RevokeMapsStableResult(string code, bool expected)
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            $$"""
            { "code": "{{code}}", "retryable": false }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardSessionClient(http);

        var revoked = await client.RevokeAsync("refresh-1", "device-a");

        Assert.Equal(expected, revoked);
    }

    [Fact]
    public async Task IdentityProviderOutagePreservesRetryability()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.ServiceUnavailable,
            """
            { "code": "IdentityProviderUnavailable", "retryable": true }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardSessionClient(http);

        var exception = await Assert.ThrowsAsync<StewardRemoteApiException>(() =>
            client.AuthenticateSteamAsync("AABBCC", "device-a"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal("IdentityProviderUnavailable", exception.Code);
        Assert.True(exception.Retryable);
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
