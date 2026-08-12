using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamLateCallGuardTests
{
    [Fact]
    public void AbandonedPendingAttemptCleansLateResultExactlyOnce()
    {
        var cleaned = new List<int>();
        var finalized = 0;
        using var registration = new TrackingDisposable();
        var attempt = new SteamLateCallGuard<int>(
            cleaned.Add,
            _ => finalized++);
        attempt.AttachRegistration(registration);

        Assert.True(attempt.TryAbandon());

        attempt.Complete(42);
        attempt.Complete(43);

        Assert.Equal([42], cleaned);
        Assert.Equal(1, finalized);
        Assert.Equal(1, registration.DisposeCount);
    }

    [Fact]
    public async Task CallbackWinningTimeoutRaceIsStillCleanedBeforeCallerReturns()
    {
        var cleaned = new List<int>();
        var finalized = 0;
        using var registration = new TrackingDisposable();
        var attempt = new SteamLateCallGuard<int>(
            cleaned.Add,
            _ => finalized++);
        attempt.AttachRegistration(registration);

        // Native callback wins the state transition, but the user-visible timeout/cancellation path
        // can still win WaitAsync. TryAbandon must report that completed race so the caller explicitly
        // cleans the already-created platform resource before surfacing the timeout.
        attempt.Complete(99);
        Assert.False(attempt.TryAbandon());

        await attempt.CleanupCompletedResultAsync();
        await attempt.CleanupCompletedResultAsync();

        Assert.Equal([99], cleaned);
        Assert.Equal(1, finalized);
        Assert.Equal(1, registration.DisposeCount);
    }

    [Fact]
    public async Task NormalCompletionDoesNotRunLateCleanup()
    {
        var cleaned = new List<int>();
        var finalized = 0;
        using var registration = new TrackingDisposable();
        using var attempt = new SteamLateCallGuard<int>(
            cleaned.Add,
            _ => finalized++);
        attempt.AttachRegistration(registration);

        attempt.Complete(7);

        Assert.Equal(7, await attempt.Completion);
        Assert.Empty(cleaned);
        Assert.Equal(0, finalized);
    }

    [Fact]
    public void AbandonedFailureFinalizesWithoutInventingAResourceToClean()
    {
        var cleaned = new List<int>();
        var finalized = 0;
        using var registration = new TrackingDisposable();
        var attempt = new SteamLateCallGuard<int>(
            cleaned.Add,
            _ => finalized++);
        attempt.AttachRegistration(registration);

        Assert.True(attempt.TryAbandon());
        attempt.Fail(new IOException("native call failed"));

        Assert.Empty(cleaned);
        Assert.Equal(1, finalized);
        Assert.Equal(1, registration.DisposeCount);
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
            => DisposeCount++;
    }
}
