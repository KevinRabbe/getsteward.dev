using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Sessions;

public enum PeerManagedHostPresenceState
{
    Starting = 1,
    Ready = 2
}

/// <summary>
/// Process-local joinability material for the currently running peer host. This is deliberately not
/// durable World metadata and is never persisted to Steam lobby data or Steam Cloud. A later peer game
/// transport may expose this endpoint only after independently authenticating and authorizing the
/// joining participant against the live World lobby.
/// </summary>
public sealed record PeerManagedHostPresence(
    WorldId WorldId,
    UserIdentity Holder,
    ulong AuthorityGeneration,
    PeerManagedHostPresenceState State,
    ManagedHostEndpoint? Endpoint,
    DateTimeOffset UpdatedAt);

public interface IPeerManagedHostPresenceRegistry
{
    Task<PeerManagedHostPresence?> GetAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default);

    Task<PeerManagedHostPresence> MarkStartingAsync(
        WorldId worldId,
        UserIdentity holder,
        ulong authorityGeneration,
        CancellationToken cancellationToken = default);

    Task<PeerManagedHostPresence> MarkReadyAsync(
        WorldId worldId,
        UserIdentity holder,
        ulong authorityGeneration,
        ManagedHostEndpoint endpoint,
        CancellationToken cancellationToken = default);

    Task EndAsync(
        WorldId worldId,
        UserIdentity holder,
        ulong authorityGeneration,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bounded process-local registry. At most one entry exists per World and every mutation is fenced by
/// exact stable holder identity plus nonzero authority generation. Ready may follow Starting or replace
/// an idempotent Ready value for the same holder/generation; it can never overwrite a different live
/// authority tuple.
/// </summary>
public sealed class PeerManagedHostPresenceRegistry : IPeerManagedHostPresenceRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<WorldId, PeerManagedHostPresence> _records = [];

    public Task<PeerManagedHostPresence?> GetAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _records.TryGetValue(worldId, out var record);
            return Task.FromResult(record);
        }
    }

    public Task<PeerManagedHostPresence> MarkStartingAsync(
        WorldId worldId,
        UserIdentity holder,
        ulong authorityGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(holder);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureGeneration(authorityGeneration);

        lock (_gate)
        {
            if (_records.TryGetValue(worldId, out var current))
            {
                EnsureSameAuthority(current, holder, authorityGeneration);
                if (current.State == PeerManagedHostPresenceState.Ready)
                {
                    throw new InvalidOperationException(
                        $"World '{worldId}' managed host is already Ready for this authority generation.");
                }
            }

            var next = new PeerManagedHostPresence(
                worldId,
                holder,
                authorityGeneration,
                PeerManagedHostPresenceState.Starting,
                Endpoint: null,
                DateTimeOffset.UtcNow);
            _records[worldId] = next;
            return Task.FromResult(next);
        }
    }

    public Task<PeerManagedHostPresence> MarkReadyAsync(
        WorldId worldId,
        UserIdentity holder,
        ulong authorityGeneration,
        ManagedHostEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(endpoint);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureGeneration(authorityGeneration);

        lock (_gate)
        {
            if (!_records.TryGetValue(worldId, out var current))
            {
                throw new InvalidOperationException(
                    $"World '{worldId}' managed host cannot become Ready before Starting was recorded.");
            }

            EnsureSameAuthority(current, holder, authorityGeneration);
            if (current.State is not (
                PeerManagedHostPresenceState.Starting or
                PeerManagedHostPresenceState.Ready))
            {
                throw new InvalidOperationException(
                    $"World '{worldId}' managed host has unsupported local presence state '{current.State}'.");
            }

            var next = new PeerManagedHostPresence(
                worldId,
                holder,
                authorityGeneration,
                PeerManagedHostPresenceState.Ready,
                endpoint,
                DateTimeOffset.UtcNow);
            _records[worldId] = next;
            return Task.FromResult(next);
        }
    }

    public Task EndAsync(
        WorldId worldId,
        UserIdentity holder,
        ulong authorityGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(holder);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureGeneration(authorityGeneration);

        lock (_gate)
        {
            if (!_records.TryGetValue(worldId, out var current))
            {
                return Task.CompletedTask;
            }

            EnsureSameAuthority(current, holder, authorityGeneration);
            _records.Remove(worldId);
            return Task.CompletedTask;
        }
    }

    private static void EnsureSameAuthority(
        PeerManagedHostPresence current,
        UserIdentity holder,
        ulong authorityGeneration)
    {
        if (current.AuthorityGeneration != authorityGeneration ||
            !SameUser(current.Holder, holder))
        {
            throw new InvalidOperationException(
                $"World '{current.WorldId}' managed host presence belongs to a different authority tuple.");
        }
    }

    private static void EnsureGeneration(ulong authorityGeneration)
    {
        if (authorityGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(authorityGeneration),
                "Managed host presence requires a nonzero peer authority generation.");
        }
    }

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}
