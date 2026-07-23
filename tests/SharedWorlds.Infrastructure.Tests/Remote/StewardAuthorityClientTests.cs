using System.Net;
using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardAuthorityClientTests
{
    [Fact]
    public async Task AcquireSendsBearerIdempotencyAndExpectedHead()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var sessionId = Guid.NewGuid();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            $$"""
            {
              "code": "ReservationAcquired",
              "data": {
                "worldId": "{{worldId.Value:D}}",
                "sessionId": "{{sessionId:D}}",
                "generation": 4,
                "holder": { "provider": "steam", "externalId": "76561198000000001" },
                "installationId": "device-a",
                "startingHead": {
                  "stateRevisionId": "{{stateId.Value:D}}",
                  "environmentRevisionId": "{{environmentId.Value:D}}"
                },
                "state": "Active",
                "acquiredAt": "2026-07-22T09:00:00Z",
                "lastHeartbeatAt": "2026-07-22T09:00:00Z",
                "becameUncertainAt": null
              },
              "retryable": false
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardAuthorityClient(http);

        var result = await client.AcquireAsync(
            worldId,
            "device-a",
            new StewardRemoteWorldHead(stateId, environmentId),
            "access-token",
            "acquire-1");

        Assert.Equal(RemoteReservationAcquireStatus.Acquired, result.Status);
        Assert.NotNull(result.Reservation);
        Assert.Equal(sessionId, result.Reservation.SessionId);
        Assert.Equal(4, result.Reservation.Generation);
        Assert.Equal(stateId, result.Reservation.StartingHead.StateRevisionId);
        Assert.Equal(environmentId, result.Reservation.StartingHead.EnvironmentRevisionId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://steward.test/api/v1/worlds/{worldId.Value:D}/reservation/acquire", request.Uri);
        Assert.Equal("Bearer access-token", request.Authorization);
        Assert.Equal("acquire-1", request.IdempotencyKey);

        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("device-a", body.RootElement.GetProperty("installationId").GetString());
        Assert.Equal(stateId.Value, body.RootElement.GetProperty("expectedStateRevisionId").GetGuid());
        Assert.Equal(environmentId.Value, body.RootElement.GetProperty("expectedEnvironmentRevisionId").GetGuid());
    }

    [Fact]
    public async Task AcquireMapsBusyReservationWithoutThrowing()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var sessionId = Guid.NewGuid();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.Conflict,
            $$"""
            {
              "code": "WorldBusy",
              "data": {
                "worldId": "{{worldId.Value:D}}",
                "sessionId": "{{sessionId:D}}",
                "generation": 8,
                "holder": { "provider": "steam", "externalId": "other-player" },
                "installationId": "device-b",
                "startingHead": {
                  "stateRevisionId": "{{stateId.Value:D}}",
                  "environmentRevisionId": null
                },
                "state": "Active",
                "acquiredAt": "2026-07-22T09:00:00Z",
                "lastHeartbeatAt": "2026-07-22T09:01:00Z",
                "becameUncertainAt": null
              },
              "retryable": false
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardAuthorityClient(http);

        var result = await client.AcquireAsync(
            worldId,
            "device-a",
            new StewardRemoteWorldHead(stateId, null),
            "access-token",
            "acquire-busy-1");

        Assert.Equal(RemoteReservationAcquireStatus.WorldBusy, result.Status);
        Assert.NotNull(result.Reservation);
        Assert.Equal("other-player", result.Reservation.HolderExternalId);
        Assert.Equal(8, result.Reservation.Generation);
    }

    [Fact]
    public async Task HeartbeatDoesNotSendIdempotencyKeyAndMapsReservationMismatch()
    {
        var worldId = WorldId.New();
        var sessionId = Guid.NewGuid();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.Conflict,
            """
            { "code": "ReservationMismatch", "retryable": false }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardAuthorityClient(http);

        var result = await client.HeartbeatAsync(
            worldId,
            "device-a",
            sessionId,
            3,
            "access-token");

        Assert.Equal(RemoteReservationHeartbeatStatus.ReservationMismatch, result);
        var request = Assert.Single(handler.Requests);
        Assert.Null(request.IdempotencyKey);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(sessionId, body.RootElement.GetProperty("sessionId").GetGuid());
        Assert.Equal(3, body.RootElement.GetProperty("generation").GetInt64());
    }

    [Fact]
    public async Task ReclaimMapsIdempotencyConflictWithoutAssumingOutcome()
    {
        var worldId = WorldId.New();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.Conflict,
            """
            { "code": "IdempotencyKeyConflict", "retryable": false }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardAuthorityClient(http);

        var result = await client.ReclaimAsync(
            worldId,
            Guid.NewGuid(),
            9,
            "access-token",
            "reclaim-1");

        Assert.Equal(RemoteReservationReclaimStatus.IdempotencyKeyConflict, result.Status);
        Assert.Equal("reclaim-1", Assert.Single(handler.Requests).IdempotencyKey);
    }

    [Fact]
    public async Task CommitSendsExactGenerationAndMapsCommittedHeads()
    {
        var worldId = WorldId.New();
        var baseState = RevisionId.New();
        var environment = RevisionId.New();
        var candidateState = RevisionId.New();
        var sessionId = Guid.NewGuid();
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            $$"""
            {
              "code": "Committed",
              "data": {
                "currentHead": {
                  "stateRevisionId": "{{candidateState.Value:D}}",
                  "environmentRevisionId": "{{environment.Value:D}}"
                },
                "candidateHead": {
                  "stateRevisionId": "{{candidateState.Value:D}}",
                  "environmentRevisionId": "{{environment.Value:D}}"
                }
              },
              "retryable": false
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardAuthorityClient(http);

        var result = await client.CommitAsync(
            worldId,
            "device-a",
            sessionId,
            11,
            new StewardRemoteWorldHead(baseState, environment),
            candidateState,
            environment,
            "access-token",
            "commit-11");

        Assert.Equal(RemoteWorldCommitStatus.Committed, result.Status);
        Assert.Equal(candidateState, result.CurrentHead!.StateRevisionId);
        Assert.Equal(environment, result.CurrentHead.EnvironmentRevisionId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("commit-11", request.IdempotencyKey);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(sessionId, body.RootElement.GetProperty("sessionId").GetGuid());
        Assert.Equal(11, body.RootElement.GetProperty("generation").GetInt64());
        Assert.Equal(baseState.Value, body.RootElement.GetProperty("expectedStateRevisionId").GetGuid());
        Assert.Equal(candidateState.Value, body.RootElement.GetProperty("candidateStateRevisionId").GetGuid());
    }

    [Fact]
    public async Task UnexpectedTransientApiFailurePreservesRetryability()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.ServiceUnavailable,
            """
            {
              "type": "urn:steward:problem:internal-failure",
              "title": "Temporary failure",
              "status": 503,
              "code": "InternalFailure",
              "retryable": true
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new StewardAuthorityClient(http);

        var exception = await Assert.ThrowsAsync<StewardRemoteApiException>(() => client.GetReservationAsync(
            WorldId.New(),
            "access-token"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal("InternalFailure", exception.Code);
        Assert.True(exception.Retryable);
    }

    [Fact]
    public async Task InvalidIdempotencyKeyIsRejectedBeforeNetworkIo()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Network must not be used."));
        using var http = CreateHttpClient(handler);
        var client = new StewardAuthorityClient(http);

        await Assert.ThrowsAsync<ArgumentException>(() => client.AcquireAsync(
            WorldId.New(),
            "device-a",
            new StewardRemoteWorldHead(RevisionId.New(), null),
            "access-token",
            "contains space"));

        Assert.Empty(handler.Requests);
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
            Requests.Add(new RequestSnapshot(
                request.Method,
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
        HttpMethod Method,
        string? Uri,
        string? Authorization,
        string? IdempotencyKey,
        string? Body);
}
