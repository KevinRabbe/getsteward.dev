using System.Net;
using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class StewardPrivateSnapshotRevisionEvidenceClientTests
{
    private static readonly DateTimeOffset RecordedAt =
        new(2026, 8, 4, 1, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task PublishesExactRecordsWithBearerAuthentication()
    {
        var fixture = Fixture.Create();
        using var api = new HttpClient(new DelegateHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                $"/api/v1/private-worlds/{fixture.WorldId.Value:D}/snapshot-revision-evidence",
                request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("access-token", request.Headers.Authorization?.Parameter);
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(
                fixture.State.Id.Value,
                body.RootElement.GetProperty("stateRevision").GetProperty("id").GetGuid());
            Assert.Equal(
                fixture.Environment.Id.Value,
                body.RootElement.GetProperty("environmentRevision").GetProperty("id").GetGuid());
            return Json(
                HttpStatusCode.OK,
                "PrivateSnapshotRevisionEvidencePublished",
                new
                {
                    worldId = fixture.WorldId.Value,
                    stateRevisionId = fixture.State.Id.Value,
                    environmentRevisionId = fixture.Environment.Id.Value,
                    recordedAt = RecordedAt
                });
        }))
        {
            BaseAddress = new Uri("https://steward.example/")
        };
        var client = CreateClient(api);

        var result = await client.PublishAsync(
            fixture.WorldId,
            fixture.State,
            fixture.Environment);

        Assert.Equal(
            RemotePrivateSnapshotRevisionEvidenceStatus.Published,
            result.Status);
        Assert.Equal(fixture.WorldId, result.WorldId);
        Assert.Equal(fixture.State.Id, result.StateRevisionId);
        Assert.Equal(fixture.Environment.Id, result.EnvironmentRevisionId);
        Assert.Equal(RecordedAt, result.RecordedAt);
    }

    [Theory]
    [InlineData(
        HttpStatusCode.OK,
        "PrivateSnapshotRevisionEvidenceAlreadyPublished",
        RemotePrivateSnapshotRevisionEvidenceStatus.AlreadyPublished)]
    [InlineData(
        HttpStatusCode.NotFound,
        "PrivateSnapshotNotFound",
        RemotePrivateSnapshotRevisionEvidenceStatus.NotFoundOrUnauthorized)]
    [InlineData(
        HttpStatusCode.BadRequest,
        "PrivateSnapshotRevisionEvidenceInvalid",
        RemotePrivateSnapshotRevisionEvidenceStatus.InvalidRequest)]
    [InlineData(
        HttpStatusCode.Conflict,
        "PrivateSnapshotRevisionEvidenceConflict",
        RemotePrivateSnapshotRevisionEvidenceStatus.Conflict)]
    public async Task MapsExactProtocolStatuses(
        HttpStatusCode httpStatus,
        string code,
        RemotePrivateSnapshotRevisionEvidenceStatus expected)
    {
        var fixture = Fixture.Create();
        var data = httpStatus == HttpStatusCode.OK
            ? new
            {
                worldId = fixture.WorldId.Value,
                stateRevisionId = fixture.State.Id.Value,
                environmentRevisionId = fixture.Environment.Id.Value,
                recordedAt = RecordedAt
            }
            : null;
        using var api = new HttpClient(new DelegateHandler(_ =>
            Task.FromResult(Json(httpStatus, code, data))))
        {
            BaseAddress = new Uri("https://steward.example/")
        };
        var client = CreateClient(api);

        var result = await client.PublishAsync(
            fixture.WorldId,
            fixture.State,
            fixture.Environment);

        Assert.Equal(expected, result.Status);
        Assert.Equal(
            httpStatus == HttpStatusCode.OK ? RecordedAt : null,
            result.RecordedAt);
    }

    [Fact]
    public async Task MismatchedSuccessEvidenceFailsClosed()
    {
        var fixture = Fixture.Create();
        using var api = new HttpClient(new DelegateHandler(_ =>
            Task.FromResult(Json(
                HttpStatusCode.OK,
                "PrivateSnapshotRevisionEvidencePublished",
                new
                {
                    worldId = WorldId.New().Value,
                    stateRevisionId = fixture.State.Id.Value,
                    environmentRevisionId = fixture.Environment.Id.Value,
                    recordedAt = RecordedAt
                }))))
        {
            BaseAddress = new Uri("https://steward.example/")
        };
        var client = CreateClient(api);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.PublishAsync(
            fixture.WorldId,
            fixture.State,
            fixture.Environment));
    }

    [Fact]
    public async Task MissingSessionFailsBeforeNetwork()
    {
        var fixture = Fixture.Create();
        var networkCalls = 0;
        using var api = new HttpClient(new DelegateHandler(_ =>
        {
            networkCalls++;
            throw new InvalidOperationException("Network must not be called.");
        }))
        {
            BaseAddress = new Uri("https://steward.example/")
        };
        var client = new StewardPrivateSnapshotRevisionEvidenceClient(
            api,
            _ => Task.FromResult<string?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PublishAsync(
            fixture.WorldId,
            fixture.State,
            fixture.Environment));

        Assert.Equal(0, networkCalls);
    }

    [Fact]
    public void RejectsNonLoopbackPlainHttpApi()
    {
        using var api = new HttpClient
        {
            BaseAddress = new Uri("http://steward.example/")
        };

        Assert.Throws<ArgumentException>(() =>
            new StewardPrivateSnapshotRevisionEvidenceClient(
                api,
                _ => Task.FromResult<string?>("token")));
    }

    private static StewardPrivateSnapshotRevisionEvidenceClient CreateClient(
        HttpClient api)
        => new(
            api,
            _ => Task.FromResult<string?>("access-token"));

    private static HttpResponseMessage Json(
        HttpStatusCode status,
        string code,
        object? data)
    {
        var payload = JsonSerializer.Serialize(
            new { code, retryable = false, data },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private sealed record Fixture(
        WorldId WorldId,
        StateRevision State,
        EnvironmentRevision Environment)
    {
        public static Fixture Create()
        {
            var worldId = WorldId.New();
            var environmentId = RevisionId.New();
            var stateId = RevisionId.New();
            var manifest = new EnvironmentManifest(
                SchemaVersion: 1,
                AdapterId: "factorio",
                GameVersion: "2.0.0",
                Components: [],
                Configuration: new Dictionary<string, string>());
            var environment = new EnvironmentRevision(
                environmentId,
                worldId,
                ParentRevisionId: RevisionId.New(),
                RecordedAt.AddMinutes(-2),
                CreatedBy: null,
                manifest);
            var state = new StateRevision(
                stateId,
                worldId,
                ParentRevisionId: RevisionId.New(),
                RecordedAt.AddMinutes(-1),
                CreatedBy: null,
                AdapterId: "factorio",
                StatePackageId: "state-package-id",
                EnvironmentRevisionId: environmentId);
            return new(worldId, state, environment);
        }
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public DelegateHandler(
            Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request);
    }
}
