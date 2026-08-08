using System.Net;
using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardLegacyAuthorityRetirementClientTests
{
    [Fact]
    public async Task GetReturnsNullOnlyForExplicitNotRetiredResponse()
    {
        var worldId = WorldId.New();
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(Path(worldId), request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("access-token", request.Headers.Authorization?.Parameter);
            return JsonResponse(
                HttpStatusCode.NotFound,
                "{\"code\":\"LegacyAuthorityNotRetired\",\"retryable\":false}");
        });
        using var http = Http(handler);
        var client = Client(http);

        Assert.Null(await client.GetAsync(worldId));
    }

    [Fact]
    public async Task GetParsesExactFrozenCanonicalHead()
    {
        var worldId = WorldId.New();
        var sessionId = Guid.NewGuid();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var retiredAt = new DateTimeOffset(2026, 8, 8, 3, 12, 0, TimeSpan.Zero);
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            SuccessJson(
                "LegacyAuthorityAlreadyRetired",
                worldId,
                sessionId,
                9,
                stateId,
                environmentId,
                retiredAt)));
        using var http = Http(handler);
        var client = Client(http);

        var evidence = await client.GetAsync(worldId);

        Assert.NotNull(evidence);
        Assert.Equal(worldId, evidence.WorldId);
        Assert.Equal(sessionId, evidence.SessionId);
        Assert.Equal(9, evidence.Generation);
        Assert.Equal(stateId, evidence.StateRevisionId);
        Assert.Equal(environmentId, evidence.EnvironmentRevisionId);
        Assert.Equal(retiredAt, evidence.RetiredAt);
    }

    [Fact]
    public async Task RetireBodyContainsOnlyReservationSessionAndGeneration()
    {
        var worldId = WorldId.New();
        var sessionId = Guid.NewGuid();
        var stateId = RevisionId.New();
        string? body = null;
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Path(worldId), request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("access-token", request.Headers.Authorization?.Parameter);
            body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(
                HttpStatusCode.OK,
                SuccessJson(
                    "LegacyAuthorityRetired",
                    worldId,
                    sessionId,
                    5,
                    stateId,
                    environmentId: null,
                    new DateTimeOffset(2026, 8, 8, 3, 15, 0, TimeSpan.Zero)));
        });
        using var http = Http(handler);
        var client = Client(http);

        var evidence = await client.RetireAsync(worldId, sessionId, 5);

        Assert.Equal(stateId, evidence.StateRevisionId);
        Assert.Null(evidence.EnvironmentRevisionId);
        Assert.NotNull(body);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(2, root.EnumerateObject().Count());
        Assert.Equal(sessionId, root.GetProperty("sessionId").GetGuid());
        Assert.Equal(5, root.GetProperty("generation").GetInt64());
        Assert.False(root.TryGetProperty("installationId", out _));
        Assert.False(root.TryGetProperty("provider", out _));
        Assert.False(root.TryGetProperty("externalId", out _));
    }

    [Fact]
    public async Task RetireRejectsEvidenceForDifferentReservation()
    {
        var worldId = WorldId.New();
        var requestedSession = Guid.NewGuid();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            SuccessJson(
                "LegacyAuthorityRetired",
                worldId,
                Guid.NewGuid(),
                6,
                RevisionId.New(),
                environmentId: null,
                new DateTimeOffset(2026, 8, 8, 3, 20, 0, TimeSpan.Zero))));
        using var http = Http(handler);
        var client = Client(http);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.RetireAsync(worldId, requestedSession, 6));
    }

    [Fact]
    public async Task GetRejectsMalformedOrWrongWorldEvidence()
    {
        var worldId = WorldId.New();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            SuccessJson(
                "LegacyAuthorityAlreadyRetired",
                WorldId.New(),
                Guid.NewGuid(),
                1,
                RevisionId.New(),
                environmentId: null,
                new DateTimeOffset(2026, 8, 8, 3, 25, 0, TimeSpan.Zero))));
        using var http = Http(handler);
        var client = Client(http);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetAsync(worldId));
    }

    [Fact]
    public async Task UnexpectedBackendCodeRemainsFailClosed()
    {
        var worldId = WorldId.New();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.Conflict,
            "{\"code\":\"ReservationMismatch\",\"retryable\":false}"));
        using var http = Http(handler);
        var client = Client(http);

        var exception = await Assert.ThrowsAsync<StewardRemoteApiException>(() =>
            client.RetireAsync(worldId, Guid.NewGuid(), 3));
        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal("ReservationMismatch", exception.Code);
        Assert.False(exception.Retryable);
    }

    private static string SuccessJson(
        string code,
        WorldId worldId,
        Guid sessionId,
        long generation,
        RevisionId stateId,
        RevisionId? environmentId,
        DateTimeOffset retiredAt)
        => JsonSerializer.Serialize(new
        {
            code,
            retryable = false,
            data = new
            {
                worldId = worldId.Value,
                sessionId,
                generation,
                stateRevisionId = stateId.Value,
                environmentRevisionId = environmentId?.Value,
                retiredAt
            }
        });

    private static string Path(WorldId worldId)
        => $"/api/v1/worlds/{worldId.Value:D}/reservation/retire-peer-authority";

    private static HttpClient Http(HttpMessageHandler handler)
        => new(handler)
        {
            BaseAddress = new Uri("https://steward.example/")
        };

    private static StewardLegacyAuthorityRetirementClient Client(HttpClient http)
        => new(http, new FixedAccessTokenProvider("access-token"));

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json)
        => new(status)
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
