using System.Net;
using System.Text;
using SharedWorlds.GameAdapters.Palworld;
using Xunit;

namespace SharedWorlds.GameAdapters.Palworld.Tests;

public sealed class PalworldRestApiClientTests
{
    [Fact]
    public async Task InfoUsesAuthenticatedDocumentedEndpointAndParsesReadinessEvidence()
    {
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.OK,
            """
            {
              "version": "v1.0.0.0",
              "servername": "Steward acceptance host",
              "description": "temporary",
              "worldguid": "A7E97BAA767DB9029EF013BB71E993A0"
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new PalworldRestApiClient(http, "admin", "secret-value");

        var info = await client.GetInfoAsync();

        Assert.Equal("v1.0.0.0", info.Version);
        Assert.Equal("Steward acceptance host", info.ServerName);
        Assert.Equal("A7E97BAA767DB9029EF013BB71E993A0", info.WorldGuid);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v1/api/info", request.Path);
        Assert.Equal("Basic YWRtaW46c2VjcmV0LXZhbHVl", request.Authorization);
    }

    [Fact]
    public async Task SettingsUsesAuthenticatedDocumentedEndpoint()
    {
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.OK,
            """
            {
              "ServerName": "Steward acceptance host",
              "RESTAPIEnabled": true,
              "RESTAPIPort": 8212,
              "BaseCampWorkerMaxNum": 20
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new PalworldRestApiClient(http, "admin", "secret-value");

        using var settings = await client.GetSettingsAsync();

        Assert.Equal("Steward acceptance host", settings.RootElement.GetProperty("ServerName").GetString());
        Assert.True(settings.RootElement.GetProperty("RESTAPIEnabled").GetBoolean());
        Assert.Equal(8212, settings.RootElement.GetProperty("RESTAPIPort").GetInt32());
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v1/api/settings", request.Path);
        Assert.Equal("Basic YWRtaW46c2VjcmV0LXZhbHVl", request.Authorization);
    }

    [Fact]
    public async Task SaveUsesDocumentedPostEndpoint()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = CreateHttpClient(handler);
        var client = new PalworldRestApiClient(http, "admin", "secret-value");

        await client.SaveAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/api/save", request.Path);
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task ShutdownSendsLengthDelimitedJsonWaitTimeAndMessage()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = CreateHttpClient(handler);
        var client = new PalworldRestApiClient(http, "admin", "secret-value");

        await client.ShutdownAsync(5, "Steward is saving this World.");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/api/shutdown", request.Path);
        Assert.Contains("\"waittime\":5", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"message\":\"Steward is saving this World.\"", request.Body, StringComparison.Ordinal);
        Assert.Equal("application/json", request.ContentType);
        Assert.NotNull(request.ContentLength);
        Assert.Equal(Encoding.UTF8.GetByteCount(request.Body!), request.ContentLength);
    }

    [Fact]
    public async Task UnauthorizedResponseFailsClosedWithoutIncludingPassword()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var http = CreateHttpClient(handler);
        var client = new PalworldRestApiClient(http, "admin", "super-secret-password");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SaveAsync());

        Assert.Contains("authentication", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("super-secret-password", exception.Message, StringComparison.Ordinal);
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
        => new(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:8212/")
        };

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json)
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
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString(),
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Content?.Headers.ContentType?.MediaType,
                request.Content?.Headers.ContentLength));
            return _responseFactory(request);
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        string Path,
        string? Authorization,
        string? Body,
        string? ContentType,
        long? ContentLength);
}
