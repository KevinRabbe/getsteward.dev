using System.Net;
using System.Text;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class StewardOwnedWorldLocationClientTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 8, 3, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ResolveUsesBearerAuthenticationAndExactRoute()
    {
        HttpRequestMessage? captured = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, "{\"code\":\"BringHereAvailability\",\"retryable\":false,\"data\":{}}");
        }))
        {
            BaseAddress = new Uri("https://safe.example/")
        };
        var client = new StewardOwnedWorldLocationClient(
            http,
            _ => Task.FromResult<string?>("token-1"));
        var worldId = WorldId.New();

        var response = await client.ResolveBringHereAsync(worldId);

        Assert.Equal("BringHereAvailability", response.Code);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal(
            $"https://safe.example/api/v1/private-worlds/{worldId.Value:D}/bring-here",
            captured.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("token-1", captured.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task TypedAvailableResponseReturnsExactImmutableSource()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        using var http = CreateHttp(BringHereResponse(
            availability: 1,
            source: Location(
                worldId,
                "source-installation",
                stateId,
                environmentId),
            conflicts: "[]",
            reason: "One exact remote head is available."));
        var client = CreateClient(http);

        var resolution = await client.ResolveBringHereAvailabilityAsync(worldId);

        Assert.Equal(BringHereAvailability.Available, resolution.Availability);
        var source = Assert.IsType<StewardOwnedWorldLocation>(resolution.Source);
        Assert.Equal(worldId, source.WorldId);
        Assert.Equal("source-installation", source.InstallationId);
        Assert.Equal(stateId, source.StateRevisionId);
        Assert.Equal(environmentId, source.EnvironmentRevisionId);
        Assert.Equal(ObservedAt, source.ObservedAt);
        Assert.Empty(resolution.ConflictingClaims);
        Assert.Equal("One exact remote head is available.", resolution.Reason);
    }

    [Fact]
    public async Task TypedConflictResponsePreservesEveryDivergentClaim()
    {
        var worldId = WorldId.New();
        var firstState = RevisionId.New();
        var firstEnvironment = RevisionId.New();
        var secondState = RevisionId.New();
        var secondEnvironment = RevisionId.New();
        var conflicts =
            $"[{Location(worldId, "installation-a", firstState, firstEnvironment)}," +
            $"{Location(worldId, "installation-b", secondState, secondEnvironment)}]";
        using var http = CreateHttp(BringHereResponse(
            availability: 3,
            source: "null",
            conflicts,
            reason: "The owned installations report divergent heads."));
        var client = CreateClient(http);

        var resolution = await client.ResolveBringHereAvailabilityAsync(worldId);

        Assert.Equal(BringHereAvailability.Conflict, resolution.Availability);
        Assert.Null(resolution.Source);
        Assert.Equal(2, resolution.ConflictingClaims.Count);
        Assert.Equal(
            new[] { "installation-a", "installation-b" },
            resolution.ConflictingClaims.Select(claim => claim.InstallationId));
    }

    [Fact]
    public async Task TypedUnavailableResponseContainsNoClaims()
    {
        var worldId = WorldId.New();
        using var http = CreateHttp(BringHereResponse(
            availability: 0,
            source: "null",
            conflicts: "[]",
            reason: "No owned installation has this World."));
        var client = CreateClient(http);

        var resolution = await client.ResolveBringHereAvailabilityAsync(worldId);

        Assert.Equal(BringHereAvailability.Unavailable, resolution.Availability);
        Assert.Null(resolution.Source);
        Assert.Empty(resolution.ConflictingClaims);
    }

    [Fact]
    public async Task UnexpectedAvailabilityCodeFailsClosed()
    {
        using var http = CreateHttp(
            "{\"code\":\"WorldLocationUpdated\",\"retryable\":false,\"data\":{}}");
        var client = CreateClient(http);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.ResolveBringHereAvailabilityAsync(WorldId.New()));

        Assert.Contains("unexpected Bring Here response", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UndefinedAvailabilityValueFailsClosed()
    {
        var worldId = WorldId.New();
        using var http = CreateHttp(BringHereResponse(
            availability: 99,
            source: "null",
            conflicts: "[]",
            reason: "Unknown."));
        var client = CreateClient(http);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.ResolveBringHereAvailabilityAsync(worldId));

        Assert.Contains("unsupported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MismatchedWorldClaimFailsClosed()
    {
        var requestedWorld = WorldId.New();
        using var http = CreateHttp(BringHereResponse(
            availability: 1,
            source: Location(
                WorldId.New(),
                "source-installation",
                RevisionId.New(),
                RevisionId.New()),
            conflicts: "[]",
            reason: "Available."));
        var client = CreateClient(http);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.ResolveBringHereAvailabilityAsync(requestedWorld));

        Assert.Contains("requested World", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AvailableWithoutSourceFailsClosed()
    {
        var worldId = WorldId.New();
        using var http = CreateHttp(BringHereResponse(
            availability: 1,
            source: "null",
            conflicts: "[]",
            reason: "Available."));
        var client = CreateClient(http);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.ResolveBringHereAvailabilityAsync(worldId));

        Assert.Contains("require one source", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConflictWithDuplicateInstallationOrIdenticalHeadsFailsClosed()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var duplicateInstallationClaims =
            $"[{Location(worldId, "installation-a", stateId, environmentId)}," +
            $"{Location(worldId, "installation-a", RevisionId.New(), RevisionId.New())}]";
        using var duplicateHttp = CreateHttp(BringHereResponse(
            availability: 3,
            source: "null",
            conflicts: duplicateInstallationClaims,
            reason: "Conflict."));
        var duplicateClient = CreateClient(duplicateHttp);

        var duplicateException = await Assert.ThrowsAsync<InvalidDataException>(() =>
            duplicateClient.ResolveBringHereAvailabilityAsync(worldId));
        Assert.Contains("duplicate installation", duplicateException.Message, StringComparison.Ordinal);

        var identicalHeadClaims =
            $"[{Location(worldId, "installation-a", stateId, environmentId)}," +
            $"{Location(worldId, "installation-b", stateId, environmentId)}]";
        using var identicalHttp = CreateHttp(BringHereResponse(
            availability: 3,
            source: "null",
            conflicts: identicalHeadClaims,
            reason: "Conflict."));
        var identicalClient = CreateClient(identicalHttp);

        var identicalException = await Assert.ThrowsAsync<InvalidDataException>(() =>
            identicalClient.ResolveBringHereAvailabilityAsync(worldId));
        Assert.Contains("divergent", identicalException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveUsesQueryCasAndNoBody()
    {
        HttpRequestMessage? captured = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            return Json(
                HttpStatusCode.OK,
                "{\"code\":\"WorldLocationUpdated\",\"retryable\":false}");
        }))
        {
            BaseAddress = new Uri("https://safe.example/")
        };
        var client = CreateClient(http);
        var world = WorldId.New();
        var state = RevisionId.New();
        var environment = RevisionId.New();

        await client.RemoveCurrentLocationAsync(world, state, environment);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Delete, captured.Method);
        Assert.Null(captured.Content);
        Assert.Contains(
            $"expectedStateRevisionId={state.Value:D}",
            captured.RequestUri!.Query,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            $"expectedEnvironmentRevisionId={environment.Value:D}",
            captured.RequestUri.Query,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConflictIsReturnedWithoutThrowing()
    {
        using var http = CreateHttp(
            "{\"code\":\"WorldLocationConflict\",\"retryable\":false,\"data\":{}}",
            HttpStatusCode.Conflict);
        var client = CreateClient(http);

        var response = await client.PublishCurrentLocationAsync(
            WorldId.New(),
            RevisionId.New(),
            RevisionId.New());

        Assert.True(response.IsConflict);
        Assert.Equal("WorldLocationConflict", response.Code);
    }

    [Fact]
    public void HttpBackendIsRejected()
    {
        using var http = new HttpClient(new StubHandler(_ =>
            throw new InvalidOperationException()))
        {
            BaseAddress = new Uri("http://unsafe.example/")
        };

        Assert.Throws<ArgumentException>(() =>
            new StewardOwnedWorldLocationClient(
                http,
                _ => Task.FromResult<string?>("token")));
    }

    [Fact]
    public async Task MissingSessionFailsBeforeNetwork()
    {
        var called = false;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            called = true;
            throw new InvalidOperationException();
        }))
        {
            BaseAddress = new Uri("https://safe.example/")
        };
        var client = new StewardOwnedWorldLocationClient(
            http,
            _ => Task.FromResult<string?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ResolveBringHereAvailabilityAsync(WorldId.New()));
        Assert.False(called);
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        var payload =
            "{\"code\":\"x\",\"retryable\":false,\"data\":\"" +
            new string('a', 1024 * 1024) +
            "\"}";
        using var http = CreateHttp(payload);
        var client = CreateClient(http);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.ResolveBringHereAvailabilityAsync(WorldId.New()));
    }

    private static StewardOwnedWorldLocationClient CreateClient(HttpClient http)
        => new(http, _ => Task.FromResult<string?>("token"));

    private static HttpClient CreateHttp(
        string body,
        HttpStatusCode status = HttpStatusCode.OK)
        => new(new StubHandler(_ => Json(status, body)))
        {
            BaseAddress = new Uri("https://safe.example/")
        };

    private static string BringHereResponse(
        int availability,
        string source,
        string conflicts,
        string reason)
        =>
            "{\"code\":\"BringHereAvailability\",\"retryable\":false,\"data\":{" +
            $"\"availability\":{availability}," +
            $"\"source\":{source}," +
            $"\"conflictingClaims\":{conflicts}," +
            $"\"reason\":\"{reason}\"" +
            "}}";

    private static string Location(
        WorldId worldId,
        string installationId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId)
        =>
            "{" +
            $"\"worldId\":\"{worldId.Value:D}\"," +
            $"\"installationId\":\"{installationId}\"," +
            $"\"stateRevisionId\":\"{stateRevisionId.Value:D}\"," +
            $"\"environmentRevisionId\":\"{environmentRevisionId.Value:D}\"," +
            $"\"observedAt\":\"{ObservedAt:O}\"" +
            "}";

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
