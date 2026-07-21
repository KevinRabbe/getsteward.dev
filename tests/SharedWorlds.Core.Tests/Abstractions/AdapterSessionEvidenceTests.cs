using SharedWorlds.Core.Abstractions;
using Xunit;

namespace SharedWorlds.Core.Tests.Abstractions;

public sealed class AdapterSessionEvidenceTests
{
    [Fact]
    public void HostReadinessCanBeRepresentedSeparatelyFromPidHandle()
    {
        var evidence = new AdapterSessionEvidence(
            AdapterSessionEvidenceFlags.LaunchRequested |
            AdapterSessionEvidenceFlags.SessionStarted |
            AdapterSessionEvidenceFlags.HostReady |
            AdapterSessionEvidenceFlags.SessionRunning,
            new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero),
            "Dedicated server answered the validated readiness probe.");

        Assert.True(evidence.ProvesRealSessionStart);
        Assert.True(evidence.ProvesHostReady);
        Assert.True(evidence.ProvesSessionRunning);
        Assert.False(evidence.ProvesNormalEnd);
        Assert.False(evidence.ProvesUnexpectedEnd);
    }

    [Fact]
    public void UnexpectedEndIsDistinctFromNormalEnd()
    {
        var evidence = new AdapterSessionEvidence(
            AdapterSessionEvidenceFlags.SessionStarted |
            AdapterSessionEvidenceFlags.EndedUnexpectedly |
            AdapterSessionEvidenceFlags.RecoveryEvidencePreserved,
            DateTimeOffset.UtcNow);

        Assert.True(evidence.ProvesRealSessionStart);
        Assert.True(evidence.ProvesUnexpectedEnd);
        Assert.False(evidence.ProvesNormalEnd);
        Assert.True(evidence.Has(AdapterSessionEvidenceFlags.RecoveryEvidencePreserved));
    }

    [Fact]
    public void SafeCaptureAndGracefulStopUseExplicitOutcomes()
    {
        var stop = AdapterGracefulStopResult.Requested();
        var safe = AdapterSafeCaptureResult.Safe();
        var waiting = AdapterSafeCaptureResult.NotYetSafe("Save files are still changing.");

        Assert.True(stop.StopWasRequested);
        Assert.True(safe.IsSafe);
        Assert.False(waiting.IsSafe);
        Assert.Equal(AdapterSafeCaptureStatus.NotYetSafe, waiting.Status);
    }
}
