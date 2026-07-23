using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Remote;

public sealed record StewardWritableReservationLease(
    WorldId WorldId,
    Guid SessionId,
    long Generation,
    string InstallationId,
    StewardRemoteWorldHead StartingHead,
    string HolderProvider,
    string HolderExternalId);

/// <summary>
/// Process-local bridge between distributed authority and remote World storage. The coordinator
/// registers the exact BE-4 reservation generation; the storage adapter reads that same lease when
/// committing the uploaded candidate and resolves it only after a successful canonical commit.
/// </summary>
public sealed class StewardWritableReservationRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<WorldId, Entry> _entries = [];
    private bool _disposed;

    public bool TryRegister(
        StewardWritableReservationLease lease,
        CancellationTokenSource heartbeatCancellation)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(heartbeatCancellation);
        ValidateLease(lease);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (_entries.ContainsKey(lease.WorldId))
            {
                return false;
            }

            _entries.Add(lease.WorldId, new Entry(lease, heartbeatCancellation));
            return true;
        }
    }

    public StewardWritableReservationLease? Get(WorldId worldId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _entries.TryGetValue(worldId, out var entry)
                ? entry.Lease
                : null;
        }
    }

    public bool TryResolve(
        WorldId worldId,
        Guid sessionId,
        long generation)
    {
        CancellationTokenSource? heartbeatCancellation = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_entries.TryGetValue(worldId, out var entry) ||
                entry.Lease.SessionId != sessionId ||
                entry.Lease.Generation != generation)
            {
                return false;
            }

            _entries.Remove(worldId);
            heartbeatCancellation = entry.HeartbeatCancellation;
        }

        heartbeatCancellation.Cancel();
        heartbeatCancellation.Dispose();
        return true;
    }

    public void Dispose()
    {
        List<CancellationTokenSource> cancellations;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellations = _entries.Values
                .Select(static entry => entry.HeartbeatCancellation)
                .ToList();
            _entries.Clear();
        }

        foreach (var cancellation in cancellations)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private static void ValidateLease(StewardWritableReservationLease lease)
    {
        if (lease.WorldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(lease));
        }

        if (lease.SessionId == Guid.Empty)
        {
            throw new ArgumentException("Session ID is required.", nameof(lease));
        }

        if (lease.Generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lease));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(lease.InstallationId);
        ArgumentNullException.ThrowIfNull(lease.StartingHead);
        ArgumentException.ThrowIfNullOrWhiteSpace(lease.HolderProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(lease.HolderExternalId);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record Entry(
        StewardWritableReservationLease Lease,
        CancellationTokenSource HeartbeatCancellation);
}
