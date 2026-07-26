using System.Net;
using Microsoft.AspNetCore.TestHost;
using SharedWorlds.Backend.Api;
using Xunit;

namespace SharedWorlds.Backend.Api.Tests;

public sealed class StewardForwardedClientAddressTests
{
    [Fact]
    public void MissingProxyConfigurationLeavesForwardingDisabled()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        Assert.False(StewardForwardedClientAddress.Configure(services, configuration));
    }

    [Fact]
    public void InvalidProxyConfigurationFailsClosed()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [StewardForwardedClientAddress.KnownProxyIpConfigurationKey] = "not-an-ip"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StewardForwardedClientAddress.Configure(services, configuration));

        Assert.Contains(
            StewardForwardedClientAddress.KnownProxyIpConfigurationKey,
            exception.Message);
    }

    [Fact]
    public async Task TrustedProxyAppliesOnlyRightmostForwardedClientHop()
    {
        await using var app = await CreateAddressProbeAsync(
            rawPeer: "10.0.0.5",
            knownProxy: "10.0.0.5");
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-Forwarded-For",
            "192.0.2.99, 198.51.100.24");

        using var response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();

        Assert.Equal("198.51.100.24", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UntrustedPeerCannotSpoofForwardedClientAddress()
    {
        await using var app = await CreateAddressProbeAsync(
            rawPeer: "10.0.0.6",
            knownProxy: "10.0.0.5");
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-Forwarded-For",
            "198.51.100.24");

        using var response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();

        Assert.Equal("10.0.0.6", await response.Content.ReadAsStringAsync());
    }

    private static async Task<WebApplication> CreateAddressProbeAsync(
        string rawPeer,
        string knownProxy)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration[StewardForwardedClientAddress.KnownProxyIpConfigurationKey] = knownProxy;

        var forwardingEnabled = StewardForwardedClientAddress.Configure(
            builder.Services,
            builder.Configuration);
        Assert.True(forwardingEnabled);

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(rawPeer);
            await next();
        });
        app.UseForwardedHeaders();
        app.MapGet("/", (HttpContext context) =>
            Results.Text(context.Connection.RemoteIpAddress?.ToString() ?? "none"));
        await app.StartAsync();
        return app;
    }
}
