using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Process-local revocation notification for one authenticated peer identity at one exact live World
/// authority generation. Canonical membership remains persisted on the World; this record exists only
/// to close the race between a live access mutation and already-authorized peer transport work.
/// </summary>
public sealed record PeerWorldLiveMemberRevocation(
    WorldId WorldId,
    ulong AuthorityGeneration,
    UserIdentity Member,
    DateTimeOffset RevokedAt);

/// <summary>
/// Small process-local deny/cancellation fence used by live peer transports. An operation obtains the
/// exact member/generation token before doing long-running peer work. Revoke atomically marks that key
/// denied, cancels every token already handed out for the key, then notifies transport owners so they
/// can close any long-lived channel that is not naturally cancellation-token scoped.
///
/// This registry is not canonical membership and is never persisted. New authority generations and
/// explicit canonical re-admission can safely use a fresh key/token.
/// </summary>
public sealed class PeerWorldLiveMemberRevocationRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<RevocationKey, RevocationEntry> _entries = [];
    private int _disposed;

    public event Action<PeerWorldLiveMemberRevocation>? Revoked;

    public CancellationToken GetCancellationToken(
        WorldId worldId,
        ulong authorityGeneration,
        UserIdentity member)
    {
        ArgumentNullException.ThrowIfNull(member);
        EnsureGeneration(authorityGeneration);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var key = CreateKey(worldId, authorityGeneration, member);
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new RevocationEntry();
                _entries.Add(key, entry);
            }

            return entry.Cancellation.Token;
        }
    }

    public bool IsRevoked(
        WorldId worldId,
        ulong authorityGeneration,
        UserIdentity member)
    {
        ArgumentNullException.ThrowIfNull(member);
        EnsureGeneration(authorityGeneration);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        lock (_gate)
        {
            return _entries.TryGetValue(
                       CreateKey(worldId, authorityGeneration, member),
                       out var entry) &&
                   entry.IsRevoked;
        }
    }

    public void ThrowIfRevoked(
        WorldId worldId,
        ulong authorityGeneration,
        UserIdentity member)
    {
        if (IsRevoked(worldId, authorityGeneration, member))
        {
            throw new UnauthorizedAccessException(
                $"Peer member '{member.ExternalId}' has been revoked from live World '{worldId}' at authority generation {authorityGeneration}.");
        }
    }

    public bool Revoke(
        WorldId worldId,
        ulong authorityGeneration,
        UserIdentity member)
    {
        ArgumentNullException.ThrowIfNull(member);
        EnsureGeneration(authorityGeneration);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        CancellationTokenSource cancellation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            var key = CreateKey(worldId, authorityGeneration, member);
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new RevocationEntry();
                _entries.Add(key, entry);
            }

            if (entry.IsRevoked)
            {
                return false;
            }

            entry.IsRevoked = true;
            cancellation = entry.Cancellation;
        }

        // Cancel before notifying channel owners. Cancellation-token-scoped operations therefore stop
        // even if a transport observer is delayed or fails independently.
        cancellation.Cancel();
        NotifyRevoked(new PeerWorldLiveMemberRevocation(
            worldId,
            authorityGeneration,
            member,
            DateTimeOffset.UtcNow));
        return true;
    }

    /// <summary>
    /// Clears only an already-revoked exact key after canonical holder-controlled re-admission. Active
    /// non-revoked tokens are intentionally left alone so a duplicate Add person cannot detach an
    /// in-flight operation from a future revocation.
    /// </summary>
    public bool Restore(
        WorldId worldId,
        ulong authorityGeneration,
        UserIdentity member)
    {
        ArgumentNullException.ThrowIfNull(member);
        EnsureGeneration(authorityGeneration);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        CancellationTokenSource? removed = null;
        lock (_gate)
        {
            var key = CreateKey(worldId, authorityGeneration, member);
            if (!_entries.TryGetValue(key, out var entry) || !entry.IsRevoked)
            {
                return false;
            }

            _entries.Remove(key);
            removed = entry.Cancellation;
        }

        removed.Dispose();
        return true;
    }

    public int ClearWorld(
        WorldId worldId,
        ulong authorityGeneration)
    {
        EnsureGeneration(authorityGeneration);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        List<CancellationTokenSource> removed = [];
        lock (_gate)
        {
            foreach (var pair in _entries.ToArray())
            {
                if (pair.Key.WorldId == worldId &&
                    pair.Key.AuthorityGeneration == authorityGeneration)
                {
                    _entries.Remove(pair.Key);
                    removed.Add(pair.Value.Cancellation);
                }
            }
        }

        foreach (var cancellation in removed)
        {
            cancellation.Dispose();
        }

        return removed.Count;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Revoked = null;
        RevocationEntry[] entries;
        lock (_gate)
        {
            entries = _entries.Values.ToArray();
            _entries.Clear();
        }

        foreach (var entry in entries)
        {
            entry.Cancellation.Cancel();
            entry.Cancellation.Dispose();
        }
    }

    private void NotifyRevoked(PeerWorldLiveMemberRevocation revocation)
    {
        var subscribers = Revoked;
        if (subscribers is null)
        {
            return;
        }

        foreach (var subscriber in subscribers.GetInvocationList()
                     .Cast<Action<PeerWorldLiveMemberRevocation>>())
        {
            try
            {
                subscriber(revocation);
            }
            catch
            {
                // The deny bit and cancellation token are already active. One channel observer must
                // never roll back or prevent another observer from receiving the revocation.
            }
        }
    }

    private static RevocationKey CreateKey(
        WorldId worldId,
        ulong authorityGeneration,
        UserIdentity member)
        => new(
            worldId,
            authorityGeneration,
            member.Provider.ToUpperInvariant(),
            member.ExternalId);

    private static void EnsureGeneration(ulong authorityGeneration)
    {
        if (authorityGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(authorityGeneration),
                "Live member revocation requires a nonzero peer authority generation.");
        }
    }

    private readonly record struct RevocationKey(
        WorldId WorldId,
        ulong AuthorityGeneration,
        string Provider,
        string ExternalId);

    private sealed class RevocationEntry
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public bool IsRevoked { get; set; }
    }
}
