namespace SharedWorlds.Desktop;

/// <summary>
/// Keeps a Steam async-call callback alive after Steward has abandoned the user-visible wait. A late
/// successful platform result is handed to the supplied cleanup action exactly once instead of
/// becoming invisible lobby membership. The caller retains abandoned guards until OnLateFinalized
/// removes them after the native callback eventually resolves.
/// </summary>
internal sealed class SteamLateCallGuard<T> : IDisposable
{
    private const int Pending = 0;
    private const int Completed = 1;
    private const int Abandoned = 2;

    private readonly TaskCompletionSource<T> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<T> _lateResultCleanup;
    private readonly Action<SteamLateCallGuard<T>> _onLateFinalized;
    private IDisposable? _registration;
    private int _state;
    private int _lateFinalized;
    private int _disposed;

    public SteamLateCallGuard(
        Action<T> lateResultCleanup,
        Action<SteamLateCallGuard<T>> onLateFinalized)
    {
        ArgumentNullException.ThrowIfNull(lateResultCleanup);
        ArgumentNullException.ThrowIfNull(onLateFinalized);
        _lateResultCleanup = lateResultCleanup;
        _onLateFinalized = onLateFinalized;
    }

    public Task<T> Completion => _completion.Task;

    public void AttachRegistration(IDisposable registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (Interlocked.CompareExchange(ref _registration, registration, null) is not null)
        {
            registration.Dispose();
            throw new InvalidOperationException("Steam callback registration was already attached.");
        }
    }

    public void Complete(T result)
    {
        var previous = Interlocked.CompareExchange(ref _state, Completed, Pending);
        if (previous == Pending)
        {
            _completion.TrySetResult(result);
            return;
        }

        if (previous == Abandoned)
        {
            _completion.TrySetResult(result);
            FinalizeLate(result);
        }
    }

    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var previous = Interlocked.CompareExchange(ref _state, Completed, Pending);
        if (previous == Pending)
        {
            _completion.TrySetException(exception);
            return;
        }

        if (previous == Abandoned)
        {
            _completion.TrySetException(exception);
            FinalizeLateWithoutResult();
        }
    }

    /// <summary>
    /// Atomically transfers ownership from the ordinary waiter to late-result cleanup. The caller
    /// must retain this guard before calling TryAbandon so the callback remains rooted after return.
    /// False means the native callback already won the race; call CleanupCompletedResultAsync before
    /// surfacing cancellation/timeout so a completed successful lobby is still abandoned.
    /// </summary>
    public bool TryAbandon()
        => Interlocked.CompareExchange(ref _state, Abandoned, Pending) == Pending;

    public async Task CleanupCompletedResultAsync()
    {
        try
        {
            var result = await _completion.Task;
            FinalizeLate(result);
        }
        catch
        {
            FinalizeLateWithoutResult();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _registration, null)?.Dispose();
    }

    private void FinalizeLate(T result)
    {
        if (Interlocked.Exchange(ref _lateFinalized, 1) != 0)
        {
            return;
        }

        try
        {
            _lateResultCleanup(result);
        }
        finally
        {
            Dispose();
            _onLateFinalized(this);
        }
    }

    private void FinalizeLateWithoutResult()
    {
        if (Interlocked.Exchange(ref _lateFinalized, 1) != 0)
        {
            return;
        }

        Dispose();
        _onLateFinalized(this);
    }
}
