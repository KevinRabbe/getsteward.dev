using Microsoft.Extensions.Configuration;
using SharedWorlds.Backend.Api;
using SharedWorlds.Backend.Identity;
using Xunit;

namespace SharedWorlds.Backend.Api.Tests;

public sealed class FriendsBuildAuthConfigurationTests
{
    [Fact]
    public void DisabledByDefaultFailsClosedEvenWithoutSteamConfiguration()
    {
        var configuration = BuildConfiguration([]);
        var verifier = FriendsBuildAuthConfiguration.CreateVerifier(configuration);

        var exception = Assert.Throws<ExternalIdentityProviderException>(() =>
            verifier.Verify(FriendsBuildCredential.Generate()));

        Assert.Equal(FriendsBuildIdentityVerifier.Provider, exception.Provider);
        Assert.False(exception.Retryable);
    }

    [Fact]
    public void ExplicitConfigurationBuildsVerifierFromHashesOnly()
    {
        var credential = FriendsBuildCredential.Generate();
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["FriendsBuild:Enabled"] = "true",
            ["FriendsBuild:Identities:0:Id"] = "friend-0001",
            ["FriendsBuild:Identities:0:DisplayName"] = "Alex",
            ["FriendsBuild:Identities:0:CredentialSha256"] =
                FriendsBuildCredential.HashForConfiguration(credential)
        });

        var verifier = FriendsBuildAuthConfiguration.CreateVerifier(configuration);
        var result = verifier.Verify(credential);

        Assert.Equal(FriendsBuildIdentityVerificationStatus.Verified, result.Status);
        Assert.Equal("friend-0001", result.Identity?.Subject.ExternalId);
        Assert.Equal("Alex", result.Identity?.DisplayName);
    }

    [Fact]
    public void EnabledWithoutCompleteIdentityConfigurationRefusesStartup()
    {
        var noIdentities = BuildConfiguration(new Dictionary<string, string?>
        {
            ["FriendsBuild:Enabled"] = "true"
        });
        var incompleteIdentity = BuildConfiguration(new Dictionary<string, string?>
        {
            ["FriendsBuild:Enabled"] = "true",
            ["FriendsBuild:Identities:0:Id"] = "friend-0001",
            ["FriendsBuild:Identities:0:DisplayName"] = "Alex"
        });

        Assert.Throws<InvalidOperationException>(() =>
            FriendsBuildAuthConfiguration.CreateVerifier(noIdentities));
        Assert.Throws<InvalidOperationException>(() =>
            FriendsBuildAuthConfiguration.CreateVerifier(incompleteIdentity));
    }

    private static IConfiguration BuildConfiguration(
        IEnumerable<KeyValuePair<string, string?>> values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
