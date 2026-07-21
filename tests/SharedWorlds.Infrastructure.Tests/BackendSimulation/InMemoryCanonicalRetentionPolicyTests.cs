using SharedWorlds.Infrastructure.BackendSimulation;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.BackendSimulation;

public sealed class InMemoryCanonicalRetentionPolicyTests
{
    [Fact]
    public void NormalRetentionKeepsCurrentPlusPreviousTwoCanonicalStates()
    {
        var policy = new InMemoryCanonicalRetentionPolicy();
        policy.RecordCanonicalState("world-1", "S0", "E0");
        policy.RecordCanonicalState("world-1", "S1", "E0");
        policy.RecordCanonicalState("world-1", "S2", "E1");
        policy.RecordCanonicalState("world-1", "S3", "E1");

        var snapshot = policy.GetSnapshot("world-1");

        Assert.Equal("S3", snapshot.CurrentStateRevisionId);
        Assert.Equal(new[] { "S1", "S2", "S3" }, snapshot.RetainedStateRevisionIds);
        Assert.Equal(new[] { "S0" }, snapshot.CleanupEligibleStateRevisionIds);
        Assert.Equal(new[] { "E0", "E1" }, snapshot.RetainedEnvironmentRevisionIds);
    }

    [Fact]
    public void PinnedOlderStateStaysRetainedOutsideNormalThreeRevisionWindow()
    {
        var policy = new InMemoryCanonicalRetentionPolicy();
        policy.RecordCanonicalState("world-1", "S0", "E0");
        policy.RecordCanonicalState("world-1", "S1", "E0");
        policy.RecordCanonicalState("world-1", "S2", "E1");
        policy.RecordCanonicalState("world-1", "S3", "E1");
        policy.RecordCanonicalState("world-1", "S4", "E2");

        Assert.True(policy.PinState("world-1", "S0"));
        var snapshot = policy.GetSnapshot("world-1");

        Assert.Equal(new[] { "S0", "S2", "S3", "S4" }, snapshot.RetainedStateRevisionIds);
        Assert.Equal(new[] { "S1" }, snapshot.CleanupEligibleStateRevisionIds);
        Assert.Equal(new[] { "E0", "E1", "E2" }, snapshot.RetainedEnvironmentRevisionIds);
    }

    [Fact]
    public void UnpinMakesOldStateAndUnreferencedEnvironmentCleanupEligibleAgain()
    {
        var policy = new InMemoryCanonicalRetentionPolicy();
        policy.RecordCanonicalState("world-1", "S0", "E-old");
        policy.RecordCanonicalState("world-1", "S1", "E1");
        policy.RecordCanonicalState("world-1", "S2", "E1");
        policy.RecordCanonicalState("world-1", "S3", "E2");
        policy.RecordCanonicalState("world-1", "S4", "E2");

        Assert.True(policy.PinState("world-1", "S0"));
        Assert.Contains("E-old", policy.GetSnapshot("world-1").RetainedEnvironmentRevisionIds);

        Assert.True(policy.UnpinState("world-1", "S0"));
        var snapshot = policy.GetSnapshot("world-1");

        Assert.Contains("S0", snapshot.CleanupEligibleStateRevisionIds);
        Assert.DoesNotContain("E-old", snapshot.RetainedEnvironmentRevisionIds);
        Assert.Equal(new[] { "S2", "S3", "S4" }, snapshot.RetainedStateRevisionIds);
    }

    [Fact]
    public void UnknownStateCannotBePinned()
    {
        var policy = new InMemoryCanonicalRetentionPolicy();
        policy.RecordCanonicalState("world-1", "S0", "E0");

        Assert.False(policy.PinState("world-1", "missing"));
    }

    [Fact]
    public void SameImmutableStateEnvironmentMappingIsIdempotent()
    {
        var policy = new InMemoryCanonicalRetentionPolicy();
        policy.RecordCanonicalState("world-1", "S0", "E0");

        policy.RecordCanonicalState("world-1", "S0", "E0");
        var snapshot = policy.GetSnapshot("world-1");

        Assert.Equal("S0", snapshot.CurrentStateRevisionId);
        Assert.Equal(new[] { "S0" }, snapshot.RetainedStateRevisionIds);
        Assert.Equal(new[] { "E0" }, snapshot.RetainedEnvironmentRevisionIds);
    }

    [Fact]
    public void ReusingImmutableStateWithDifferentEnvironmentIsRejectedWithoutMutation()
    {
        var policy = new InMemoryCanonicalRetentionPolicy();
        policy.RecordCanonicalState("world-1", "S0", "E0");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            policy.RecordCanonicalState("world-1", "S0", "E-other"));
        var snapshot = policy.GetSnapshot("world-1");

        Assert.Contains("different environment revision", exception.Message, StringComparison.Ordinal);
        Assert.Equal("S0", snapshot.CurrentStateRevisionId);
        Assert.Equal(new[] { "S0" }, snapshot.RetainedStateRevisionIds);
        Assert.Equal(new[] { "E0" }, snapshot.RetainedEnvironmentRevisionIds);
    }
}
