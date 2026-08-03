using System.Net;
using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class StewardOwnedWorldLocationPresentationClientTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 8, 3, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PresentedPublicationSendsBoundedWorldIdentity()
    {
        string? requestBody = null;
        using var http = new HttpClient(new StubHandler(async (request, cancellationToken) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json("{\"code\":\"WorldLocationCreated\",\"retryable\":false}");
        }))
        {
            BaseAddress = new Uri("https://safe.example/")
        };
        var client = CreateClient(http);
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();

        await client.PublishCurrentLocationWithPresentationAsync(
            worldId,
            stateId,
            environmentId,
            "Factory World",
            "factorio");

        using var document = JsonDocument.Parse(requestBody!);
        var root = document.RootElement;
        Assert.Equal("Factory World", root.GetProperty("worldName").GetString());
        Assert.Equal("factorio", root.GetProperty("gameAdapterId").GetString());
        Assert.Equal(
            stateId.Value,
            root.GetProperty("stateRevisionId").GetGuid());
        Assert.Equal(
            environmentId.Value,
            root.GetProperty("environmentRevisionId").GetGuid());
    }

    [Fact]
    public async Task TypedAvailabilityPreservesOptionalPresentation()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        using var http = CreateHttp(
            BringHereResponse(
                worldId,
                stateId,
                environmentId,
                "\"Factory World\"",
                "\"factorio\""));
        var client = CreateClient(http);

        var resolution = await client.ResolveBringHereAvailabilityAsync(worldId);

        var presentation = Assert.IsType<StewardOwnedWorldPresentation>(
            resolution.Source!.Presentation);
        Assert.Equal("Factory World", presentation.Name);
        Assert.Equal("factorio", presentation.GameAdapterId);
    }

    [Fact]
    public async Task PartialPresentationResponseFailsClosed()
    {
        var worldId = WorldId.New();
        using var http = CreateHttp(
            BringHereResponse(
                worldId,
                RevisionId.New(),
                RevisionId.New(),
                "\"Factory World\"",
                "null"));
        var client = CreateClient(http);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.ResolveBringHereAvailabilityAsync(worldId));

        Assert.Contains(
            "both World name and game adapter ID",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyAvailabilityWithoutPresentationRemainsValid()
    {
        var worldId = WorldId.New();
        using var http = CreateHttp(
            BringHereResponse(
                worldId,
                RevisionId.New(),
                RevisionId.New(),
                "null",
                "null"));
        var client = CreateClient(http);

        var resolution = await client.ResolveBringHereAvailabilityAsync(worldId);

        Assert.Null(resolution.Source!.Presentation);
    }

    private static StewardOwnedWorldLocationClient CreateClient(HttpClient http)
        => new(http, _ => Task.FromResult<string?>("access-token"));

    private static HttpClient CreateHttp(string body)
        => new(new StubHandler((_, _) => Task.FromResult(Json(body))))
        {
            BaseAddress = new Uri("https://safe.example/")
        };

    private static string BringHereResponse(
        WorldId worldId,
        RevisionId stateId,
        RevisionId environmentId,
        string worldName,
        string gameAdapterId)
        =>
            "{\"code\":\"BringHereAvailability\",\"retryable\":false,\"data\":{" +
            "\"availability\":1," +
            "\"source\":{" +
            $"\"worldId\":\"{worldId.Value:D}\"," +
            "\"installationId\":\"pc-a\"," +
            $"\"stateRevisionId\":\"{stateId.Value:D}\"," +
            $"\"environmentRevisionId\":\"{environmentId.Value:D}\"," +
            $"\"observedAt\":\"{ObservedAt:O}\"," +
            $"\"worldName\":{worldName}," +
            $"\"gameAdapterId\":{gameAdapterId}" +
            "},\"conflictingClaims\":[]," +
            "\"reason\":\"One exact remote head is available.\"}}";

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}
