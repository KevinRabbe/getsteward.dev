using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class StewardOwnedWorldLocationPublicationTriggerTests
{
    [Fact]
    public async Task SignalsWhileRunningCollapseIntoOneFollowUpPass()
    {
        var firstStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        using var trigger = new StewardOwnedWorldLocationPublicationTrigger(
            async cancellationToken =>
            {
                var execution = Interlocked.Increment(ref executions);
                if (execution == 1)
                {
                    firstStarted.SetResult(true);
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                    return;
                }

                secondCompleted.SetResult(true);
            });

        trigger.Request();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var index = 0; index < 100; index++)
        {
            trigger.Request();
        }

        releaseFirst.SetResult(true);
        await secondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Assert.Equal(2, Volatile.Read(ref executions));
        Assert.Null(trigger.LastFailure);
    }

    [Fact]
    public async Task FailureIsObservedWithoutStartingAnAutomaticRetryLoop()
    {
        var failureObserved = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        using var trigger = new StewardOwnedWorldLocationPublicationTrigger(
            _ =>
            {
                var execution = Interlocked.Increment(ref executions);
                if (execution == 1)
                {
                    throw new InvalidOperationException("Injected publication failure.");
                }

                secondCompleted.SetResult(true);
                return Task.CompletedTask;
            },
            exception => failureObserved.SetResult(exception));

        trigger.Request();
        var failure = await failureObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref executions));
        Assert.Same(failure, trigger.LastFailure);

        trigger.Request();
        await secondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, Volatile.Read(ref executions));
        Assert.Null(trigger.LastFailure);
    }

    [Fact]
    public async Task DisposeCancelsTheSingleWorkerAndIgnoresLaterSignals()
    {
        var started = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var trigger = new StewardOwnedWorldLocationPublicationTrigger(
            async cancellationToken =>
            {
                Interlocked.Increment(ref executions);
                started.SetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancelled.SetResult(true);
                    }
                }
            });

        trigger.Request();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        trigger.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        trigger.Request();
        await Task.Delay(100);

        Assert.Equal(1, Volatile.Read(ref executions));
    }
}
