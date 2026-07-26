using SharedWorlds.Backend.Identity;
using Xunit;

namespace SharedWorlds.Backend.Tests.Identity;

public sealed class FriendsBuildIdentityVerifierTests
{
    [Fact]
    public void GeneratedCredentialVerifiesToStableConfiguredIdentity()
    {
        var credential = FriendsBuildCredential.Generate();
        var verifier = new FriendsBuildIdentityVerifier(
        [
            new FriendsBuildIdentityDefinition(
                "friend-0001",
                "Alex",
                FriendsBuildCredential.HashForConfiguration(credential))
        ]);

        var result = verifier.Verify(credential);

        Assert.Equal(FriendsBuildIdentityVerificationStatus.Verified, result.Status);
        var identity = Assert.IsType<VerifiedExternalIdentity>(result.Identity);
        Assert.Equal(FriendsBuildIdentityVerifier.Provider, identity.Subject.Provider);
        Assert.Equal("friend-0001", identity.Subject.ExternalId);
        Assert.Equal("Alex", identity.DisplayName);
    }

    [Fact]
    public void WrongOrMalformedCredentialDoesNotAuthenticate()
    {
        var credential = FriendsBuildCredential.Generate();
        var verifier = new FriendsBuildIdentityVerifier(
        [
            new FriendsBuildIdentityDefinition(
                "friend-0001",
                "Alex",
                FriendsBuildCredential.HashForConfiguration(credential))
        ]);

        var wrongCredential = FriendsBuildCredential.Generate();
        var wrong = verifier.Verify(wrongCredential);
        var malformed = verifier.Verify("not-a-friends-build-secret");

        Assert.Equal(FriendsBuildIdentityVerificationStatus.InvalidCredential, wrong.Status);
        Assert.Null(wrong.Identity);
        Assert.Equal(FriendsBuildIdentityVerificationStatus.InvalidCredential, malformed.Status);
        Assert.Null(malformed.Identity);
    }

    [Fact]
    public void UnconfiguredDeploymentFailsClosedAndNonRetryable()
    {
        var verifier = FriendsBuildIdentityVerifier.CreateUnavailable();

        var exception = Assert.Throws<ExternalIdentityProviderException>(() =>
            verifier.Verify(FriendsBuildCredential.Generate()));

        Assert.Equal(FriendsBuildIdentityVerifier.Provider, exception.Provider);
        Assert.False(exception.Retryable);
    }

    [Fact]
    public void DuplicateIdentityOrCredentialConfigurationIsRejected()
    {
        var first = FriendsBuildCredential.Generate();
        var second = FriendsBuildCredential.Generate();

        Assert.Throws<ArgumentException>(() => new FriendsBuildIdentityVerifier(
        [
            new FriendsBuildIdentityDefinition(
                "friend-0001",
                "Alex",
                FriendsBuildCredential.HashForConfiguration(first)),
            new FriendsBuildIdentityDefinition(
                "friend-0001",
                "Max",
                FriendsBuildCredential.HashForConfiguration(second))
        ]));

        Assert.Throws<ArgumentException>(() => new FriendsBuildIdentityVerifier(
        [
            new FriendsBuildIdentityDefinition(
                "friend-0001",
                "Alex",
                FriendsBuildCredential.HashForConfiguration(first)),
            new FriendsBuildIdentityDefinition(
                "friend-0002",
                "Max",
                FriendsBuildCredential.HashForConfiguration(first))
        ]));
    }

    [Fact]
    public void MalformedCredentialHashConfigurationIsRejectedBeforeRuntime()
    {
        Assert.Throws<ArgumentException>(() => new FriendsBuildIdentityDefinition(
            "friend-0001",
            "Alex",
            "not-a-sha256"));
    }

    [Fact]
    public void CredentialProvisioningProducesExpectedPrivateFormat()
    {
        var credential = FriendsBuildCredential.Generate();
        var digest = FriendsBuildCredential.HashForConfiguration(credential);

        Assert.StartsWith("st_friend_", credential, StringComparison.Ordinal);
        Assert.Equal(52, credential.Length);
        Assert.Equal(64, digest.Length);
        Assert.All(digest, character => Assert.True(Uri.IsHexDigit(character)));
    }
}
