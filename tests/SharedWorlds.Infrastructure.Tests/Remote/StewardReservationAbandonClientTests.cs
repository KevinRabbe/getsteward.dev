using System.Net;
using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardReservationAbandonClientTests
{
    [Theory]
    [InlineData("ReservationAbandoned", RemoteReservationAbandonStatus.Abandoned)]
    [InlineData("ReservationNoLongerCurrent", RemoteReservationAbandonStatus.NoLongerCurrent)]
    public async Task AbandonSendsExactReservationAndMapsConvergentResult(
        string code,
        RemoteReservationAbandonStatus expected)
    {
        var worldId = WorldId.New();
        var sessionId = Guid.NewGuid();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            $$"""
            { "code": "{{code}}", "retryable": false }
            """));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://steward.test/")
        };
        var client = new StewardReservationAbandonClient(http);

        var result = await client.AbandonAsync(
            worldId,
            "device-a",
            sessionId,
            7,
            "access-token");

        Assert.Equal(expected, result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            $"https://steward.test/api/v1/worlds/{worldId.Value:D}/reservation/abandon",
            request.Uri);
        Assert.Equal("Bearer access-token", request.Authorization);
        Assert.Null(request.IdempotencyKey);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("device-a", body.RootElement.GetProperty("installationId").GetString());
        Assert.Equal(sessionId, body.RootElement.GetProperty("sessionId").GetGuid());
        Assert.Equal(7, body.RootElement.GetProperty("generation").GetInt64());
    }

    [Fact]
    public async Task TransientFailurePreservesRetryability()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.ServiceUnavailable,
            """
            { "code": "InternalFailure", "retryable": true }
            """));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://steward.test/")
        };
        var client = new StewardReservationAbandonClient(http);

        var exception = await Assert.ThrowsAsync<StewardRemoteApiException>(() => client.AbandonAsync(
            WorldId.New(),
            "device-a",
            Guid.NewGuid(),
            1,
            "access-token"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal("InternalFailure", exception.Code);
        Assert.True(exception.Retryable);
    }

    [Fact]
    public async Task InvalidGenerationIsRejectedBeforeNetworkIo()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Network must not be used."));
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://steward.test/")
        };
        var client = new StewardReservationAbandonClient(http);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.AbandonAsync(
            WorldId.New(),
            "device-a",
            Guid.NewGuid(),
            0,
            "access-token"));
        Assert.Empty(handler.Requests);
    }

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
            Requests.Add(new RequestSnapshot(
                request.RequestUri?.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Idempotency-Key", out var values)
                    ? values.Single()
                    : null,
                body));
            return _responseFactory(request);
        }
    }

    private sealed record RequestSnapshot(
        string? Uri,
        string? Authorization,
        string? IdempotencyKey,
        string? Body);
}
