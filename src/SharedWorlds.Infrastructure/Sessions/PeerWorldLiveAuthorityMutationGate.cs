namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Process-local serialization boundary for live peer authority mutations that must not overlap.
/// Steward's managed writable-session gate already permits only one writable managed Host lifecycle in
/// this process, so one small gate is sufficient for the live handoff/member-mutation race.
/// </summary>
public sealed class PeerWorldLiveAuthorityMutationGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public async ValueTask<IDisposable> EnterAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            await _gate.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(PeerWorldLiveAuthorityMutationGate));
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            _gate.Release();
            throw new ObjectDisposedException(nameof(PeerWorldLiveAuthorityMutationGate));
        }

        return new Releaser(_gate);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Wake blocked entrants but keep the semaphore itself alive for leases that were already issued;
        // their final Release must remain safe while runtime teardown unwinds concurrently.
        _lifetime.Cancel();
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Releaser(SemaphoreSlim gate)
            => _gate = gate;

        public void Dispose()
            => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
