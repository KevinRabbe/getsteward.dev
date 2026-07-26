using System.Collections.Concurrent;
using System.Net;
using System.Text;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardWorldSessionHostPresenceTests
{
    [Fact]
    public async Task ReadyPresenceRefreshesOnExistingReservationHeartbeatAndThenClears()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var sessionId = Guid.NewGuid();
        var presenceRefreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var metadataHttp = Client(_ => JsonResponse(
            HttpStatusCode.OK,
            WorldFoundJson(worldId, stateId)));
        var authorityHandler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/reservation/acquire", StringComparison.Ordinal))
            {
                return JsonResponse(
                    HttpStatusCode.OK,
                    ReservationJson(worldId, stateId, sessionId));
            }

            if (request.RequestUri.AbsolutePath.EndsWith("/reservation/heartbeat", StringComparison.Ordinal))
            {
                return JsonResponse(
                    HttpStatusCode.OK,
                    """{ "code": "HeartbeatAccepted", "retryable": false }""");
            }

            throw new InvalidOperationException($"Unexpected authority request {request.RequestUri}.");
        });
        using var authorityHttp = Http(authorityHandler);
        using var abandonHttp = Client(_ => JsonResponse(
            HttpStatusCode.OK,
            """{ "code": "ReservationAbandoned", "retryable": false }"""));

        var presenceHandler = new PresenceRecordingHandler((method, requestCount) =>
        {
            if (method == HttpMethod.Put)
            {
                if (requestCount >= 3)
                {
                    presenceRefreshed.TrySetResult();
                }

                return JsonResponse(
                    HttpStatusCode.OK,
                    """{ "code": "HostPresencePublished", "retryable": false }""");
            }

            if (method == HttpMethod.Delete)
            {
                return JsonResponse(
                    HttpStatusCode.OK,
                    """{ "code": "HostPresenceCleared", "retryable": false }""");
            }

            throw new InvalidOperationException($"Unexpected host-presence method {method}.");
        });
        using var presenceHttp = Http(presenceHandler);
        using var registry = new StewardWritableReservationRegistry();
        var coordinator = new StewardWorldSessionCoordinator(
            new StewardWorldMetadataClient(metadataHttp),
            new StewardAuthorityClient(authorityHttp),
            new StewardReservationAbandonClient(abandonHttp),
            new StaticTokenProvider(),
            new EmptyRecoveryStore(),
            registry,
            "device-a",
            new StewardWorldSessionCoordinatorOptions(
                TimeSpan.FromMilliseconds(10),
                acquireTransportAttempts: 1,
                headRefreshAttempts: 1),
            new StewardHostPresenceClient(presenceHttp));
        var user = new UserIdentity("friends-build", "friend-0001", "Kevin");

        await coordinator.AcquireHostAsync(worldId, user);
        await coordinator.MarkHostStartingAsync(worldId);
        await coordinator.MarkHostReadyAsync(
            worldId,
            new ManagedHostEndpoint(34197, "session-secret"));
        await presenceRefreshed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await coordinator.EndHostPresenceAsync(worldId);
        await coordinator.ReleaseHostAsync(worldId, user);

        var snapshots = presenceHandler.Requests.ToArray();
        Assert.True(snapshots.Count(snapshot => snapshot.Method == "PUT") >= 3);
        Assert.Contains(snapshots, snapshot =>
            snapshot.Method == "PUT" &&
            snapshot.Body?.Contains("\"state\":\"Starting\"", StringComparison.Ordinal) == true);
        Assert.Contains(snapshots, snapshot =>
            snapshot.Method == "PUT" &&
            snapshot.Body?.Contains("\"state\":\"Ready\"", StringComparison.Ordinal) == true &&
            snapshot.Body.Contains("\"address\":null", StringComparison.Ordinal) &&
            snapshot.Body.Contains("\"port\":34197", StringComparison.Ordinal) &&
            snapshot.Body.Contains("\"joinToken\":\"session-secret\"", StringComparison.Ordinal));
        var clear = Assert.Single(snapshots.Where(snapshot => snapshot.Method == "DELETE"));
        Assert.EndsWith(
            $"/host-presence/{sessionId:D}/3",
            clear.Path,
            StringComparison.Ordinal);
        Assert.Null(registry.Get(worldId));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
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
            "accessManager": { "provider": "friends-build", "externalId": "friend-0001" },
            "createdAt": "2026-07-26T08:00:00Z",
            "updatedAt": "2026-07-26T09:00:00Z"
          },
          "retryable": false
        }
        """;

    private static string ReservationJson(WorldId worldId, RevisionId stateId, Guid sessionId)
        => $$"""
        {
          "code": "ReservationAcquired",
          "data": {
            "worldId": "{{worldId.Value:D}}",
            "sessionId": "{{sessionId:D}}",
            "generation": 3,
            "holder": { "provider": "friends-build", "externalId": "friend-0001" },
            "installationId": "device-a",
            "startingHead": {
              "stateRevisionId": "{{stateId.Value:D}}",
              "environmentRevisionId": null
            },
            "state": "Active",
            "acquiredAt": "2026-07-26T09:00:00Z",
            "lastHeartbeatAt": "2026-07-26T09:00:00Z",
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

    private sealed class EmptyRecoveryStore : IWorkspaceRecoveryStore
    {
        public Task SaveAsync(
            WorkspaceRecoveryRecord record,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveAsync(
            WorkspaceId workspaceId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<WorkspaceRecoveryRecord>> ListAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRecoveryRecord>>([]);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_responseFactory(request));
    }

    private sealed class PresenceRecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpMethod, int, HttpResponseMessage> _responseFactory;
        private int _putCount;

        public PresenceRecordingHandler(Func<HttpMethod, int, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public ConcurrentQueue<RequestSnapshot> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var count = request.Method == HttpMethod.Put
                ? Interlocked.Increment(ref _putCount)
                : Volatile.Read(ref _putCount);
            Requests.Enqueue(new RequestSnapshot(
                request.Method.Method,
                request.RequestUri?.AbsolutePath,
                body));
            return _responseFactory(request.Method, count);
        }
    }

    private sealed record RequestSnapshot(
        string Method,
        string? Path,
        string? Body);
}
