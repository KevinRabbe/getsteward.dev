using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Core.Tests;

public sealed class StableIdentitySetFingerprintTests
{
    [Fact]
    public void FingerprintIgnoresOrderingDisplayNameAndProviderCase()
    {
        var first = StableIdentitySetFingerprint.Compute(
        [
            new UserIdentity("Steam", "200", "Old Name"),
            new UserIdentity("steam", "100", "Alice")
        ]);
        var second = StableIdentitySetFingerprint.Compute(
        [
            new UserIdentity("STEAM", "100", "Renamed"),
            new UserIdentity("steam", "200", "Different Name")
        ]);

        Assert.Equal(first, second);
        Assert.True(StableIdentitySetFingerprint.IsCanonicalFingerprint(first));
    }

    [Fact]
    public void FingerprintChangesWhenStableMembershipChanges()
    {
        var oneMember = StableIdentitySetFingerprint.Compute(
        [new UserIdentity("steam", "100", "Alice")]);
        var twoMembers = StableIdentitySetFingerprint.Compute(
        [
            new UserIdentity("steam", "100", "Alice"),
            new UserIdentity("steam", "200", "Bob")
        ]);

        Assert.NotEqual(oneMember, twoMembers);
    }

    [Fact]
    public void DuplicateStableIdentityDoesNotChangeSetFingerprint()
    {
        var single = StableIdentitySetFingerprint.Compute(
        [("steam", "100")]);
        var duplicate = StableIdentitySetFingerprint.Compute(
        [("STEAM", "100"), ("steam", "100")]);

        Assert.Equal(single, duplicate);
    }
}
