using System.Net;
using System.Text;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class StewardOwnedPrivateWorldCatalogClientTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 8, 3, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AvailableEntryParsesThroughExactAuthenticatedRoute()
    {
        HttpRequestMessage? captured = null;
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        using var http = CreateHttp(request =>
        {
            captured = request;
            return CatalogResponse(
                Entry(
                    worldId,
                    "Factory World",
                    "factorio",
                    availability: 1,
                    source: Location(worldId, "pc-source", stateId, environmentId),
                    conflicts: "[]"));
        });
        var client = CreateClient(http);

        var entry = Assert.Single(await client.ListAsync());

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal(
            "https://safe.example/api/v1/private-worlds",
            captured.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("access-token", captured.Headers.Authorization.Parameter);
        Assert.Equal(worldId, entry.WorldId);
        Assert.Equal("Factory World", entry.Name);
        Assert.Equal("factorio", entry.GameAdapterId);
        Assert.Equal(BringHereAvailability.Available, entry.Availability);
        Assert.Equal("pc-source", entry.Source!.InstallationId);
    }

    [Fact]
    public async Task AdapterConflictAllowsNullAdapterWithoutSelectingSource()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var conflicts =
            $"[{Location(worldId, "pc-a", stateId, environmentId, "World", "factorio")}," +
            $"{Location(worldId, "pc-b", stateId, environmentId, "World", "other.adapter")}]";
        using var http = CreateHttp(_ => CatalogResponse(
            Entry(
                worldId,
                "World",
                gameAdapterId: null,
                availability: 3,
                source: "null",
                conflicts)));
        var client = CreateClient(http);

        var entry = Assert.Single(await client.ListAsync());

        Assert.Equal(BringHereAvailability.Conflict, entry.Availability);
        Assert.Null(entry.GameAdapterId);
        Assert.Null(entry.Source);
        Assert.Equal(2, entry.ConflictingClaims.Count);
    }

    [Fact]
    public async Task HeadConflictRequiresDivergentHeadsWhenAdapterIsKnown()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var conflicts =
            $"[{Location(worldId, "pc-a", stateId, environmentId)}," +
            $"{Location(worldId, "pc-b", stateId, environmentId)}]";
        using var http = CreateHttp(_ => CatalogResponse(
            Entry(
                worldId,
                "World",
                "factorio",
                availability: 3,
                source: "null",
                conflicts)));
        var client = CreateClient(http);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAsync());

        Assert.Contains("divergent", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateWorldIdsFailClosed()
    {
        var worldId = WorldId.New();
        var entry = Entry(
            worldId,
            "World",
            "factorio",
            availability: 1,
            source: Location(worldId, "pc-a", RevisionId.New(), RevisionId.New()),
            conflicts: "[]");
        using var http = CreateHttp(_ => CatalogResponse($"{entry},{entry}"));
        var client = CreateClient(http);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAsync());

        Assert.Contains("duplicate World IDs", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StringAvailabilityAndUnavailableEntriesFailClosed()
    {
        var worldId = WorldId.New();
        var stringEntry =
            "{" +
            $"\"worldId\":\"{worldId.Value:D}\"," +
            "\"name\":\"World\"," +
            "\"gameAdapterId\":\"factorio\"," +
            "\"availability\":\"Available\"," +
            "\"source\":null,\"conflictingClaims\":[],\"reason\":\"x\"}";
        using var stringHttp = CreateHttp(_ => CatalogResponse(stringEntry));
        var stringClient = CreateClient(stringHttp);
        await Assert.ThrowsAsync<InvalidDataException>(() => stringClient.ListAsync());

        using var unavailableHttp = CreateHttp(_ => CatalogResponse(
            Entry(
                WorldId.New(),
                "World",
                "factorio",
                availability: 0,
                source: "null",
                conflicts: "[]")));
        var unavailableClient = CreateClient(unavailableHttp);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            unavailableClient.ListAsync());
        Assert.Contains("must not be included", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingSessionFailsBeforeNetworkAccess()
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
        var client = new StewardOwnedPrivateWorldCatalogClient(
            http,
            _ => Task.FromResult<string?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListAsync());
        Assert.False(called);
    }

    private static StewardOwnedPrivateWorldCatalogClient CreateClient(HttpClient http)
        => new(http, _ => Task.FromResult<string?>("access-token"));

    private static HttpClient CreateHttp(Func<HttpRequestMessage, HttpResponseMessage> handler)
        => new(new StubHandler(handler))
        {
            BaseAddress = new Uri("https://safe.example/")
        };

    private static HttpResponseMessage CatalogResponse(string entries)
        => Json(
            "{\"code\":\"OwnedPrivateWorldCatalog\",\"retryable\":false," +
            $"\"data\":{{\"worlds\":[{entries}]}}}}");

    private static string Entry(
        WorldId worldId,
        string name,
        string? gameAdapterId,
        int availability,
        string source,
        string conflicts)
        =>
            "{" +
            $"\"worldId\":\"{worldId.Value:D}\"," +
            $"\"name\":\"{name}\"," +
            $"\"gameAdapterId\":{(gameAdapterId is null ? "null" : $"\"{gameAdapterId}\"")}," +
            $"\"availability\":{availability}," +
            $"\"source\":{source}," +
            $"\"conflictingClaims\":{conflicts}," +
            "\"reason\":\"Catalog decision.\"" +
            "}";

    private static string Location(
        WorldId worldId,
        string installationId,
        RevisionId stateId,
        RevisionId environmentId,
        string? worldName = "World",
        string? gameAdapterId = "factorio")
        =>
            "{" +
            $"\"worldId\":\"{worldId.Value:D}\"," +
            $"\"installationId\":\"{installationId}\"," +
            $"\"stateRevisionId\":\"{stateId.Value:D}\"," +
            $"\"environmentRevisionId\":\"{environmentId.Value:D}\"," +
            $"\"observedAt\":\"{ObservedAt:O}\"," +
            $"\"worldName\":{(worldName is null ? "null" : $"\"{worldName}\"")}," +
            $"\"gameAdapterId\":{(gameAdapterId is null ? "null" : $"\"{gameAdapterId}\"")}" +
            "}";

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK)
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
