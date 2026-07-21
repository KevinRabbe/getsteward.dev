using System.Net;
using System.Text;
using SharedWorlds.Backend.Identity;
using Xunit;

namespace SharedWorlds.Backend.Tests.Identity;

public sealed class SteamWebApiTicketVerifierTests
{
    [Fact]
    public async Task ValidTicketDerivesVerifiedSteamIdentityFromSteamResponse()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """
            {
              "response": {
                "params": {
                  "result": "OK",
                  "steamid": "76561198012345678",
                  "ownersteamid": "76561198012345678"
                }
              }
            }
            """));
        using var client = new HttpClient(handler);
        var verifier = CreateVerifier(client);

        var result = await verifier.VerifyAsync("A1B2C3D4");
        var identity = Assert.IsType<VerifiedExternalIdentity>(result.Identity);

        Assert.Equal(ExternalIdentityTicketVerificationStatus.Verified, result.Status);
        Assert.Equal("steam", identity.Subject.Provider);
        Assert.Equal("76561198012345678", identity.Subject.ExternalId);
        Assert.Null(identity.DisplayName);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);

        var requestUri = Assert.IsType<Uri>(handler.LastRequestUri);
        Assert.Equal("partner.steam-api.com", requestUri.Host);
        Assert.Equal("/ISteamUserAuth/AuthenticateUserTicket/v1/", requestUri.AbsolutePath);
        var query = ParseQuery(requestUri);
        Assert.Equal("publisher-secret", query["key"]);
        Assert.Equal("480", query["appid"]);
        Assert.Equal("A1B2C3D4", query["ticket"]);
        Assert.Equal("steward-backend", query["identity"]);
    }

    [Fact]
    public async Task MalformedTicketIsRejectedLocallyWithoutContactingSteam()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Must not call Steam."));
        using var client = new HttpClient(handler);
        var verifier = CreateVerifier(client);

        var oddLength = await verifier.VerifyAsync("ABC");
        var notHex = await verifier.VerifyAsync("NOTHEX");
        var empty = await verifier.VerifyAsync(" ");

        Assert.Equal(ExternalIdentityTicketVerificationStatus.InvalidTicket, oddLength.Status);
        Assert.Equal(ExternalIdentityTicketVerificationStatus.InvalidTicket, notHex.Status);
        Assert.Equal(ExternalIdentityTicketVerificationStatus.InvalidTicket, empty.Status);
        Assert.Null(oddLength.Identity);
        Assert.Null(notHex.Identity);
        Assert.Null(empty.Identity);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SteamRejectedTicketReturnsExpectedInvalidTicketOutcome()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """
            {
              "response": {
                "params": {
                  "result": "Invalid"
                }
              }
            }
            """));
        using var client = new HttpClient(handler);
        var verifier = CreateVerifier(client);

        var result = await verifier.VerifyAsync("A1B2");

        Assert.Equal(ExternalIdentityTicketVerificationStatus.InvalidTicket, result.Status);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task NonSuccessHttpResponseIsProviderFailureWithoutSecretInException()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        var verifier = CreateVerifier(client);

        var exception = await Assert.ThrowsAsync<ExternalIdentityProviderException>(() =>
            verifier.VerifyAsync("A1B2"));

        Assert.Equal("steam", exception.Provider);
        Assert.Contains("503", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("publisher-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidJsonIsProviderFailureRatherThanInvalidIdentity()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json", Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler);
        var verifier = CreateVerifier(client);

        var exception = await Assert.ThrowsAsync<ExternalIdentityProviderException>(() =>
            verifier.VerifyAsync("A1B2"));

        Assert.Equal("steam", exception.Provider);
        Assert.Contains("invalid response", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OkResponseWithoutSteamIdIsProviderProtocolFailure()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            """
            {
              "response": {
                "params": {
                  "result": "OK"
                }
              }
            }
            """));
        using var client = new HttpClient(handler);
        var verifier = CreateVerifier(client);

        var exception = await Assert.ThrowsAsync<ExternalIdentityProviderException>(() =>
            verifier.VerifyAsync("A1B2"));

        Assert.Equal("steam", exception.Provider);
        Assert.Contains("incomplete response", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransportFailureDoesNotExposePublisherCredential()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("network unavailable"));
        using var client = new HttpClient(handler);
        var verifier = CreateVerifier(client);

        var exception = await Assert.ThrowsAsync<ExternalIdentityProviderException>(() =>
            verifier.VerifyAsync("A1B2"));

        Assert.Equal("steam", exception.Provider);
        Assert.DoesNotContain("publisher-secret", exception.ToString(), StringComparison.Ordinal);
    }

    private static SteamWebApiTicketVerifier CreateVerifier(HttpClient client)
        => new(
            client,
            new SteamWebApiTicketVerifierOptions(
                appId: 480,
                publisherApiKey: "publisher-secret",
                identity: "steward-backend"));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = separator >= 0 ? pair[..separator] : pair;
            var value = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
            result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
        }

        return result;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        public int CallCount { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastMethod = request.Method;
            LastRequestUri = request.RequestUri;
            return Task.FromResult(_handler(request));
        }
    }
}
