using SharedWorlds.Infrastructure.BackendSimulation;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.BackendSimulation;

public sealed class InMemoryCandidateRetentionPolicyTests
{
    [Fact]
    public void UnresolvedLocalCandidateNeverExpiresOnlyBecauseTimePassed()
    {
        var policy = new InMemoryCandidateRetentionPolicy();
        var created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        policy.Track("candidate-1", SimulatedCandidateRetentionKind.UnresolvedLocal, created);

        var snapshot = Assert.IsType<SimulatedCandidateRetentionSnapshot>(
            policy.GetSnapshot("candidate-1", created.AddYears(10)));

        Assert.False(snapshot.CleanupEligible);
    }

    [Fact]
    public void ExplicitlyAbandonedLocalCandidateUsesSevenDayRecoveryGrace()
    {
        var policy = new InMemoryCandidateRetentionPolicy();
        var abandonedAt = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        policy.Track(
            "candidate-1",
            SimulatedCandidateRetentionKind.ExplicitlyAbandonedLocal,
            abandonedAt);

        Assert.False(Assert.IsType<SimulatedCandidateRetentionSnapshot>(
            policy.GetSnapshot("candidate-1", abandonedAt.AddDays(6))).CleanupEligible);
        Assert.True(Assert.IsType<SimulatedCandidateRetentionSnapshot>(
            policy.GetSnapshot("candidate-1", abandonedAt.AddDays(7))).CleanupEligible);
    }

    [Fact]
    public void RemoteCandidateRemainsWhileRecoveryPinnedEvenAfterNormalGrace()
    {
        var policy = new InMemoryCandidateRetentionPolicy();
        var created = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        policy.Track(
            "candidate-1",
            SimulatedCandidateRetentionKind.VerifiedRemoteUncommitted,
            created,
            recoveryPinned: true);

        Assert.False(Assert.IsType<SimulatedCandidateRetentionSnapshot>(
            policy.GetSnapshot("candidate-1", created.AddDays(30))).CleanupEligible);

        Assert.True(policy.SetRecoveryPinned("candidate-1", false));
        Assert.True(Assert.IsType<SimulatedCandidateRetentionSnapshot>(
            policy.GetSnapshot("candidate-1", created.AddDays(30))).CleanupEligible);
    }

    [Fact]
    public void PartialTransferBecomesCleanupEligibleAfterTwentyFourHoursOfInactivity()
    {
        var policy = new InMemoryCandidateRetentionPolicy();
        var activity = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        policy.Track(
            "transfer-1",
            SimulatedCandidateRetentionKind.PartialTransfer,
            activity);

        Assert.False(Assert.IsType<SimulatedCandidateRetentionSnapshot>(
            policy.GetSnapshot("transfer-1", activity.AddHours(23))).CleanupEligible);
        Assert.True(Assert.IsType<SimulatedCandidateRetentionSnapshot>(
            policy.GetSnapshot("transfer-1", activity.AddHours(24))).CleanupEligible);
    }

    [Fact]
    public void NewActivityRestartsTimedCleanupWindow()
    {
        var policy = new InMemoryCandidateRetentionPolicy();
        var created = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        policy.Track(
            "transfer-1",
            SimulatedCandidateRetentionKind.PartialTransfer,
            created);

        var laterActivity = created.AddHours(20);
        Assert.True(policy.RecordActivity("transfer-1", laterActivity));

        Assert.False(Assert.IsType<SimulatedCandidateRetentionSnapshot>(
            policy.GetSnapshot("transfer-1", created.AddHours(30))).CleanupEligible);
        Assert.True(Assert.IsType<SimulatedCandidateRetentionSnapshot>(
            policy.GetSnapshot("transfer-1", laterActivity.AddHours(24))).CleanupEligible);
    }

    [Theory]
    [InlineData(SimulatedCandidateRetentionKind.CommittedTemporary)]
    [InlineData(SimulatedCandidateRetentionKind.UnchangedTemporary)]
    public void DurableCompletedTemporaryCandidateIsImmediatelyCleanupEligible(
        SimulatedCandidateRetentionKind kind)
    {
        var policy = new InMemoryCandidateRetentionPolicy();
        var now = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        policy.Track("candidate-1", kind, now);

        Assert.True(Assert.IsType<SimulatedCandidateRetentionSnapshot>(
            policy.GetSnapshot("candidate-1", now)).CleanupEligible);
    }
}
