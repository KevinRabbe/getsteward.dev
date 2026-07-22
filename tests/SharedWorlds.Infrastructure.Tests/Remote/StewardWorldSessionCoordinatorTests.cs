using System.Net;
using System.Text;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Sessions;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardWorldSessionCoordinatorTests
{
    [Fact]
    public async Task AcquireStartsHeartbeatAndSafeReleaseAbandonsExactLease()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var sessionId = Guid.NewGuid();
        var heartbeatObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new StewardWorldMetadataClient(Client(request => JsonResponse(
            HttpStatusCode.OK,
            WorldFoundJson(worldId, stateId))));
        var authorityHandler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/reservation/acquire", StringComparison.Ordinal))
            {
                return JsonResponse(
                    HttpStatusCode.OK,
                    ReservationJson("ReservationAcquired", worldId, stateId, sessionId, "device-a", 3, "Active"));
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/reservation/heartbeat", StringComparison.Ordinal))
            {
                heartbeatObserved.TrySetResult();
                return JsonResponse(
                    HttpStatusCode.OK,
                    """{ "code": "HeartbeatAccepted", "retryable": false }""");
            }

            throw new InvalidOperationException($"Unexpected authority request {request.RequestUri}.");
        });
        using var authorityHttp = Http(authorityHandler);
        var abandonHandler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """{ "code": "ReservationAbandoned", "retryable": false }"""));
        using var abandonHttp = Http(abandonHandler);
        using var registry = new StewardWritableReservationRegistry();
        var coordinator = Coordinator(
            metadata,
            new StewardAuthorityClient(authorityHttp),
            new StewardReservationAbandonClient(abandonHttp),
            new RecordingRecoveryStore(),
            registry,
            heartbeatInterval: TimeSpan.FromMilliseconds(10));
        var user = new UserIdentity("steam", "76561198000000001", "Tester");

        var session = await coordinator.AcquireHostAsync(worldId, user);
        await heartbeatObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(SessionState.Hosting, session.State);
        var lease = Assert.IsType<StewardWritableReservationLease>(registry.Get(worldId));
        Assert.Equal(sessionId, lease.SessionId);
        Assert.Equal(3, lease.Generation);

        await coordinator.ReleaseHostAsync(worldId, user);

        Assert.Null(registry.Get(worldId));
        var abandon = Assert.Single(abandonHandler.Requests);
        Assert.EndsWith($"/worlds/{worldId.Value:D}/reservation/abandon", abandon.Uri, StringComparison.Ordinal);
        Assert.Contains($"\"sessionId\":\"{sessionId:D}\"", abandon.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"generation\":3", abandon.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecoveryPendingKeepsExactReservationAndDoesNotAbandon()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var sessionId = Guid.NewGuid();
        var metadata = new StewardWorldMetadataClient(Client(_ => JsonResponse(
            HttpStatusCode.OK,
            WorldFoundJson(worldId, stateId))));
        using var authority = Client(request => request.RequestUri!.AbsolutePath.EndsWith("/reservation/acquire", StringComparison.Ordinal)
            ? JsonResponse(
                HttpStatusCode.OK,
                ReservationJson("ReservationAcquired", worldId, stateId, sessionId, "device-a", 4, "Active"))
            : JsonResponse(HttpStatusCode.OK, """{ "code": "HeartbeatAccepted", "retryable": false }"""));
        var abandonHandler = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """{ "code": "ReservationAbandoned", "retryable": false }"""));
        using var abandonHttp = Http(abandonHandler);
        var user = new UserIdentity("steam", "76561198000000001", "Tester");
        var recovery = new RecordingRecoveryStore();
        recovery.Records.Add(new WorkspaceRecoveryRecord(
            WorkspaceId.New(),
            worldId,
            stateId,
            "factorio",
            "workspace",
            user,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            WorkspaceRecoveryStatus.RecoveryPending,
            "commit failed"));
        using var registry = new StewardWritableReservationRegistry();
        var coordinator = Coordinator(
            metadata,
            new StewardAuthorityClient(authority),
            new StewardReservationAbandonClient(abandonHttp),
            recovery,
            registry,
            heartbeatInterval: TimeSpan.FromHours(1));

        await coordinator.AcquireHostAsync(worldId, user);
        await coordinator.ReleaseHostAsync(worldId, user);

        Assert.NotNull(registry.Get(worldId));
        Assert.Empty(abandonHandler.Requests);
    }

    [Fact]
    public async Task AlreadyHeldByCallerFromDifferentInstallationIsNotAdopted()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var metadata = new StewardWorldMetadataClient(Client(_ => JsonResponse(
            HttpStatusCode.OK,
            WorldFoundJson(worldId, stateId))));
        using var authority = Client(_ => JsonResponse(
            HttpStatusCode.OK,
            ReservationJson(
                "ReservationAlreadyHeldByCaller",
                worldId,
                stateId,
                Guid.NewGuid(),
                "device-b",
                5,
                "Active")));
        using var abandon = Client(_ => throw new InvalidOperationException("Abandon must not run."));
        using var registry = new StewardWritableReservationRegistry();
        var coordinator = Coordinator(
            metadata,
            new StewardAuthorityClient(authority),
            new StewardReservationAbandonClient(abandon),
            new RecordingRecoveryStore(),
            registry,
            heartbeatInterval: TimeSpan.FromHours(1));

        var exception = await Assert.ThrowsAsync<StewardWorldUnavailableException>(() =>
            coordinator.AcquireHostAsync(
                worldId,
                new UserIdentity("steam", "76561198000000001", "Tester")));

        Assert.Equal("HeldByAnotherInstallation", exception.Code);
        Assert.Null(registry.Get(worldId));
    }

    [Fact]
    public async Task UnknownAcquireOutcomeNeverRegistersLeaseOrStartsGameplayAuthorityLocally()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var metadata = new StewardWorldMetadataClient(Client(_ => JsonResponse(
            HttpStatusCode.OK,
            WorldFoundJson(worldId, stateId))));
        var authorityHandler = new RecordingHandler(_ => throw new HttpRequestException("network down"));
        using var authorityHttp = Http(authorityHandler);
        using var abandon = Client(_ => throw new InvalidOperationException("Abandon must not run."));
        using var registry = new StewardWritableReservationRegistry();
        var coordinator = Coordinator(
            metadata,
            new StewardAuthorityClient(authorityHttp),
            new StewardReservationAbandonClient(abandon),
            new RecordingRecoveryStore(),
            registry,
            heartbeatInterval: TimeSpan.FromHours(1),
            acquireTransportAttempts: 2);

        var exception = await Assert.ThrowsAsync<StewardReservationOutcomeUnknownException>(() =>
            coordinator.AcquireHostAsync(
                worldId,
                new UserIdentity("steam", "76561198000000001", "Tester")));

        Assert.Equal(worldId, exception.WorldId);
        Assert.Equal(2, authorityHandler.Requests.Count);
        Assert.Null(registry.Get(worldId));
        Assert.Equal(
            authorityHandler.Requests[0].IdempotencyKey,
            authorityHandler.Requests[1].IdempotencyKey);
    }

    private static StewardWorldSessionCoordinator Coordinator(
        StewardWorldMetadataClient worlds,
        StewardAuthorityClient authority,
        StewardReservationAbandonClient abandon,
        IWorkspaceRecoveryStore recovery,
        StewardWritableReservationRegistry registry,
        TimeSpan heartbeatInterval,
        int acquireTransportAttempts = 1)
        => new(
            worlds,
            authority,
            abandon,
            new StaticTokenProvider(),
            recovery,
            registry,
            "device-a",
            new StewardWorldSessionCoordinatorOptions(
                heartbeatInterval,
                acquireTransportAttempts,
                headRefreshAttempts: 2));

    private static HttpClient Client(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        => Http(new RecordingHandler(responseFactory));

    private static HttpClient Http(HttpMessageHandler handler)
        => new(handler)
        {
            BaseAddress = new Uri("https://steward.test/")
        };

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string WorldFoundJson(WorldId worldId, RevisionId stateId)
        => $$"""
        {
          "code": "WorldFound",
          "data": {
            "worldId": "{{worldId.Value:D}}",
            "adapterId": "factorio",
            "displayName": "Factory",
            "currentStateRevisionId": "{{stateId.Value:D}}",
            "currentEnvironmentRevisionId": null,
            "accessManager": { "provider": "steam", "externalId": "76561198000000001" },
            "createdAt": "2026-07-22T08:00:00Z",
            "updatedAt": "2026-07-22T09:00:00Z"
          },
          "retryable": false
        }
        """;

    private static string ReservationJson(
        string code,
        WorldId worldId,
        RevisionId stateId,
        Guid sessionId,
        string installationId,
        long generation,
        string state)
        => $$"""
        {
          "code": "{{code}}",
          "data": {
            "worldId": "{{worldId.Value:D}}",
            "sessionId": "{{sessionId:D}}",
            "generation": {{generation}},
            "holder": { "provider": "steam", "externalId": "76561198000000001" },
            "installationId": "{{installationId}}",
            "startingHead": {
              "stateRevisionId": "{{stateId.Value:D}}",
              "environmentRevisionId": null
            },
            "state": "{{state}}",
            "acquiredAt": "2026-07-22T09:00:00Z",
            "lastHeartbeatAt": "2026-07-22T09:00:00Z",
            "becameUncertainAt": null
          },
          "retryable": false
        }
        """;

    private sealed class StaticTokenProvider : IStewardAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult("access-token");
    }

    private sealed class RecordingRecoveryStore : IWorkspaceRecoveryStore
    {
        public List<WorkspaceRecoveryRecord> Records { get; } = [];

        public Task SaveAsync(
            WorkspaceRecoveryRecord record,
            CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(existing => existing.Id == record.Id);
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            WorkspaceId workspaceId,
            CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(record => record.Id == workspaceId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkspaceRecoveryRecord>> ListAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRecoveryRecord>>(Records.ToArray());
    }

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
                request.Headers.TryGetValues("Idempotency-Key", out var values)
                    ? values.Single()
                    : null,
                body));
            return _responseFactory(request);
        }
    }

    private sealed record RequestSnapshot(
        string? Uri,
        string? IdempotencyKey,
        string? Body);
}
