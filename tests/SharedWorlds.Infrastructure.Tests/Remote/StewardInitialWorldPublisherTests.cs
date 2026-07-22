using System.Net;
using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardInitialWorldPublisherTests
{
    [Fact]
    public async Task NewWorldPublishesExactEnvironmentAndReusesAlreadyPublishedState()
    {
        var fixture = CreateFixture();
        var api = new PublicationHandler(fixture, worldInitiallyExists: false);
        using var apiClient = CreateHttpClient(api);
        using var transferClient = new HttpClient(new RejectNetworkHandler());
        var service = CreateService(apiClient, transferClient);
        await using var package = Package();

        var remote = await service.PublishAsync(
            fixture.World,
            fixture.Environment,
            fixture.State,
            package,
            fixture.User);

        Assert.Equal(fixture.World.Id, remote.WorldId);
        Assert.Equal(fixture.World.CurrentStateRevisionId, remote.CurrentStateRevisionId);
        Assert.Equal(fixture.World.CurrentEnvironmentRevisionId, remote.CurrentEnvironmentRevisionId);
        Assert.Equal(1, api.CreateCount);
        Assert.Equal(1, api.EnvironmentPublishCount);
        Assert.Equal(1, api.TransferBeginCount);
        Assert.Equal(1, api.CurrentRevisionReadCount);
        Assert.Equal(1, api.EnvironmentReadCount);
    }

    [Fact]
    public async Task ExistingMatchingWorldResumesWithoutCreatingSecondWorld()
    {
        var fixture = CreateFixture();
        var api = new PublicationHandler(fixture, worldInitiallyExists: true);
        using var apiClient = CreateHttpClient(api);
        using var transferClient = new HttpClient(new RejectNetworkHandler());
        var service = CreateService(apiClient, transferClient);
        await using var package = Package();

        await service.PublishAsync(
            fixture.World,
            fixture.Environment,
            fixture.State,
            package,
            fixture.User);

        Assert.Equal(0, api.CreateCount);
        Assert.Equal(1, api.EnvironmentPublishCount);
        Assert.Equal(1, api.TransferBeginCount);
    }

    [Fact]
    public async Task ExistingWorldWithDifferentHeadFailsBeforePublishingAnything()
    {
        var fixture = CreateFixture();
        var api = new PublicationHandler(
            fixture,
            worldInitiallyExists: true,
            remoteStateOverride: RevisionId.New());
        using var apiClient = CreateHttpClient(api);
        using var transferClient = new HttpClient(new RejectNetworkHandler());
        var service = CreateService(apiClient, transferClient);
        await using var package = Package();

        var exception = await Assert.ThrowsAsync<StewardInitialWorldPublicationException>(() =>
            service.PublishAsync(
                fixture.World,
                fixture.Environment,
                fixture.State,
                package,
                fixture.User));

        Assert.Equal("ExistingWorldConflict", exception.Code);
        Assert.Equal(0, api.EnvironmentPublishCount);
        Assert.Equal(0, api.TransferBeginCount);
    }

    [Fact]
    public async Task DifferentAccessManagerCannotAdoptSameWorldId()
    {
        var fixture = CreateFixture();
        var api = new PublicationHandler(
            fixture,
            worldInitiallyExists: true,
            managerExternalId: "76561198099999999");
        using var apiClient = CreateHttpClient(api);
        using var transferClient = new HttpClient(new RejectNetworkHandler());
        var service = CreateService(apiClient, transferClient);
        await using var package = Package();

        var exception = await Assert.ThrowsAsync<StewardInitialWorldPublicationException>(() =>
            service.PublishAsync(
                fixture.World,
                fixture.Environment,
                fixture.State,
                package,
                fixture.User));

        Assert.Equal("AccessManagerMismatch", exception.Code);
        Assert.Equal(0, api.EnvironmentPublishCount);
    }

    private static StewardInitialWorldPublisher CreateService(
        HttpClient apiClient,
        HttpClient transferClient)
    {
        var metadata = new StewardWorldMetadataClient(apiClient);
        return new StewardInitialWorldPublisher(
            new StewardWorldCreationClient(apiClient),
            metadata,
            new StewardPackageUploadClient(apiClient, transferClient),
            new StaticTokenProvider());
    }

    private static Fixture CreateFixture()
    {
        var worldId = WorldId.New();
        var environmentId = RevisionId.New();
        var stateId = RevisionId.New();
        var user = new UserIdentity("steam", "76561198000000001", "Tester");
        var manifest = new EnvironmentManifest(
            1,
            "factorio",
            "2.0.72",
            [new EnvironmentComponent(
                "mod",
                "base",
                "2.0.72",
                "builtin",
                new Dictionary<string, string> { ["channel"] = "stable" })],
            new Dictionary<string, string> { ["startup"] = "ABCDEF" });
        var world = new World(
            worldId,
            "Factory",
            "factorio",
            [user],
            environmentId,
            stateId);
        var environment = new EnvironmentRevision(
            environmentId,
            worldId,
            null,
            DateTimeOffset.UtcNow,
            user,
            manifest);
        var state = new StateRevision(
            stateId,
            worldId,
            null,
            DateTimeOffset.UtcNow,
            user,
            "factorio",
            "local-package");
        return new Fixture(world, environment, state, user);
    }

    private static MemoryStream Package()
        => new(Encoding.UTF8.GetBytes("immutable-factorio-world"), writable: false);

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
        => new(handler)
        {
            BaseAddress = new Uri("https://steward.test/")
        };

    private sealed record Fixture(
        World World,
        EnvironmentRevision Environment,
        StateRevision State,
        UserIdentity User);

    private sealed class StaticTokenProvider : IStewardAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult("access-token");
    }

    private sealed class PublicationHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly Fixture _fixture;
        private readonly RevisionId _remoteState;
        private readonly string _managerExternalId;
        private bool _worldExists;

        public PublicationHandler(
            Fixture fixture,
            bool worldInitiallyExists,
            RevisionId? remoteStateOverride = null,
            string? managerExternalId = null)
        {
            _fixture = fixture;
            _worldExists = worldInitiallyExists;
            _remoteState = remoteStateOverride ?? fixture.State.Id;
            _managerExternalId = managerExternalId ?? fixture.User.ExternalId;
        }

        public int CreateCount { get; private set; }
        public int EnvironmentPublishCount { get; private set; }
        public int TransferBeginCount { get; private set; }
        public int CurrentRevisionReadCount { get; private set; }
        public int EnvironmentReadCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var method = request.Method;
            var worldBase = $"/api/v1/worlds/{_fixture.World.Id.Value:D}";
            var environmentPath =
                $"{worldBase}/revisions/{_fixture.Environment.Id.Value:D}/environment";

            if (method == HttpMethod.Get && path == worldBase)
            {
                return Task.FromResult(_worldExists
                    ? WorldFound()
                    : Envelope(HttpStatusCode.NotFound, "WorldNotFound"));
            }

            if (method == HttpMethod.Post && path == "/api/v1/worlds")
            {
                CreateCount++;
                _worldExists = true;
                return Task.FromResult(Envelope(HttpStatusCode.Created, "WorldCreated"));
            }

            if (method == HttpMethod.Post && path == environmentPath)
            {
                EnvironmentPublishCount++;
                return Task.FromResult(Envelope(HttpStatusCode.OK, "EnvironmentRevisionPublished"));
            }

            if (method == HttpMethod.Post && path == $"{worldBase}/transfers")
            {
                TransferBeginCount++;
                return Task.FromResult(Envelope(HttpStatusCode.OK, "AlreadyPublished"));
            }

            if (method == HttpMethod.Get && path == $"{worldBase}/current-revision")
            {
                CurrentRevisionReadCount++;
                return Task.FromResult(Envelope(
                    HttpStatusCode.OK,
                    "CurrentRevisionFound",
                    new
                    {
                        world = WorldDto(),
                        state = new
                        {
                            revisionId = _fixture.State.Id.Value,
                            byteSize = 24,
                            sha256 = new string('A', 64),
                            requiredEnvironmentRevisionId = _fixture.Environment.Id.Value,
                            publishedAt = DateTimeOffset.UtcNow
                        },
                        environment = (object?)null
                    }));
            }

            if (method == HttpMethod.Get && path == environmentPath)
            {
                EnvironmentReadCount++;
                return Task.FromResult(Envelope(
                    HttpStatusCode.OK,
                    "EnvironmentRevisionFound",
                    new
                    {
                        revisionId = _fixture.Environment.Id.Value,
                        artifactReference = $"manifest:{_fixture.Environment.Id.Value:D}",
                        byteSize = (long?)null,
                        sha256 = (string?)null,
                        publishedAt = DateTimeOffset.UtcNow,
                        manifest = _fixture.Environment.Manifest
                    }));
            }

            throw new InvalidOperationException($"Unexpected request {method} {path}.");
        }

        private HttpResponseMessage WorldFound()
            => Envelope(HttpStatusCode.OK, "WorldFound", WorldDto());

        private object WorldDto()
            => new
            {
                worldId = _fixture.World.Id.Value,
                adapterId = _fixture.World.GameAdapterId,
                displayName = _fixture.World.Name,
                currentStateRevisionId = _remoteState.Value,
                currentEnvironmentRevisionId = _fixture.Environment.Id.Value,
                accessManager = new
                {
                    provider = _fixture.User.Provider,
                    externalId = _managerExternalId
                },
                createdAt = DateTimeOffset.UtcNow,
                updatedAt = DateTimeOffset.UtcNow
            };

        private static HttpResponseMessage Envelope(
            HttpStatusCode statusCode,
            string code,
            object? data = null)
        {
            var json = JsonSerializer.Serialize(
                new { code, data, retryable = false },
                JsonOptions);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                $"Direct object-storage network must not be used when state is already published: {request.RequestUri}");
    }
}
