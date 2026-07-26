using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using SharedWorlds.Backend.Api;
using SharedWorlds.Backend.Identity;

namespace SharedWorlds.Backend.Api.Tests;

public sealed class FriendsBuildRosterApiTests
{
    [Fact]
    public async Task AuthenticatedFriendsBuildSessionReadsNamesWithoutCredentialMaterial()
    {
        var store = new ApiTestHarness.InMemorySessionStore();
        var clock = new ApiTestHarness.TestClock();
        var sessions = new StewardSessionService(
            store,
            () => clock.Now,
            tokenGenerator: new ApiTestHarness.DeterministicTokenGenerator());
        var firstCredential = FriendsBuildCredential.Generate();
        var secondCredential = FriendsBuildCredential.Generate();
        var verifier = new FriendsBuildIdentityVerifier(
        [
            new FriendsBuildIdentityDefinition(
                "friend-0001",
                "Kevin",
                FriendsBuildCredential.HashForConfiguration(firstCredential)),
            new FriendsBuildIdentityDefinition(
                "friend-0002",
                "Alex",
                FriendsBuildCredential.HashForConfiguration(secondCredential))
        ]);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton(verifier);
        await using var app = builder.Build();
        app.MapFriendsBuildAuthApiV1();
        await app.StartAsync();

        var tokens = await sessions.CreateSessionAsync(
            new VerifiedExternalIdentity(
                new ExternalIdentityRef(FriendsBuildIdentityVerifier.Provider, "friend-0001"),
                "Kevin"),
            "installation-1");
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        using var response = await client.GetAsync("/api/v1/auth/friends/identities");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("FriendsBuildIdentitiesFound", json, StringComparison.Ordinal);
        Assert.Contains("friend-0001", json, StringComparison.Ordinal);
        Assert.Contains("Kevin", json, StringComparison.Ordinal);
        Assert.Contains("friend-0002", json, StringComparison.Ordinal);
        Assert.Contains("Alex", json, StringComparison.Ordinal);
        Assert.DoesNotContain(firstCredential, json, StringComparison.Ordinal);
        Assert.DoesNotContain(secondCredential, json, StringComparison.Ordinal);
        Assert.DoesNotContain("credentialSha256", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credentialHash", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RosterRequiresAuthenticatedFriendsBuildIdentity()
    {
        var store = new ApiTestHarness.InMemorySessionStore();
        var clock = new ApiTestHarness.TestClock();
        var sessions = new StewardSessionService(
            store,
            () => clock.Now,
            tokenGenerator: new ApiTestHarness.DeterministicTokenGenerator());
        var credential = FriendsBuildCredential.Generate();
        var verifier = new FriendsBuildIdentityVerifier(
        [
            new FriendsBuildIdentityDefinition(
                "friend-0001",
                "Kevin",
                FriendsBuildCredential.HashForConfiguration(credential))
        ]);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton(verifier);
        await using var app = builder.Build();
        app.MapFriendsBuildAuthApiV1();
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var unauthenticated = await client.GetAsync("/api/v1/auth/friends/identities");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        var steamTokens = await sessions.CreateSessionAsync(
            new VerifiedExternalIdentity(new ExternalIdentityRef("steam", "76561198000000001"), "Steam User"),
            "installation-2");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", steamTokens.AccessToken);

        using var wrongProvider = await client.GetAsync("/api/v1/auth/friends/identities");
        Assert.Equal(HttpStatusCode.Forbidden, wrongProvider.StatusCode);
    }
}
