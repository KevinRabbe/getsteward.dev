using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using SharedWorlds.Backend.Api;
using SharedWorlds.Backend.Identity;
using Xunit;

namespace SharedWorlds.Backend.Api.Tests;

public sealed class StewardUnavailableSteamApiTests
{
    [Fact]
    public async Task UnconfiguredSteamAuthenticationReturnsNonRetryableProviderUnavailable()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new StewardSessionService(
            new ApiTestHarness.InMemorySessionStore(),
            () => new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero),
            tokenGenerator: new ApiTestHarness.DeterministicTokenGenerator()));
        builder.Services.AddSingleton(
            SteamWebApiTicketVerifier.CreateUnavailable(
                new HttpClient(new ThrowingHandler())));

        await using var app = builder.Build();
        app.UseStewardApiProblemHandling();
        app.MapStewardApiV1();
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/steam/session",
            new
            {
                ticketHex = "AABBCC",
                installationId = "test-installation"
            });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal("IdentityProviderUnavailable", root.GetProperty("code").GetString());
        Assert.False(root.GetProperty("retryable").GetBoolean());
        Assert.Contains(
            "not configured",
            root.GetProperty("title").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException(
                "E4-A must not contact Steam when publisher credentials are intentionally absent.");
    }
}
