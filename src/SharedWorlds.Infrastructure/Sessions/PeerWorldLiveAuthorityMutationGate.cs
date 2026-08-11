namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Process-local serialization boundary for live peer authority mutations that must not overlap.
/// Steward's managed writable-session gate already permits only one writable managed Host lifecycle in
/// this process, so one small gate is sufficient for the live handoff-request/member-removal race.
/// Long state transfer is not held under this gate: once RequestHandoff publishes RequestedHost, later
/// member removal observes that lobby transition and refuses.
/// </summary>
public sealed class PeerWorldLiveAuthorityMutationGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public async ValueTask<IDisposable> EnterAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        await _gate.WaitAsync(cancellationToken);
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

        _gate.Dispose();
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
