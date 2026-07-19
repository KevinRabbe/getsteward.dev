using System.Collections.Concurrent;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;
using SharedWorlds.Core.Sessions;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Process-local session coordination used by the current single-machine product slice.
/// A future remote coordinator can replace this implementation without changing Core semantics.
/// </summary>
public sealed class LocalWorldSessionCoordinator : IWorldSessionCoordinator
{
    private readonly ConcurrentDictionary<WorldId, WorldSession> _sessions = new();

    public Task<WorldSession> GetSessionAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_sessions.GetOrAdd(worldId, CreateAvailable));
    }

    public Task<WorldSession> AcquireHostAsync(
        WorldId worldId,
        UserIdentity user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = _sessions.GetOrAdd(worldId, CreateAvailable);

            if (current.State == SessionState.Hosting && current.ActiveHost == user)
            {
                return Task.FromResult(current);
            }

            if (current.State != SessionState.Available)
            {
                throw new WorldSessionConflictException(
                    worldId,
                    $"Not available for hosting. Current state: {current.State}.");
            }

            var next = current with
            {
                State = SessionState.Hosting,
                ActiveHost = user,
                RequestedHost = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            if (_sessions.TryUpdate(worldId, next, current))
            {
                return Task.FromResult(next);
            }
        }
    }

    public Task RequestHandoffAsync(
        WorldId worldId,
        UserIdentity requestedHost,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedHost);
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            var current = _sessions.GetOrAdd(worldId, CreateAvailable);
            if (current.State != SessionState.Hosting || current.ActiveHost is null)
            {
                throw new WorldSessionConflictException(
                    worldId,
                    "There is no active host to hand off from.");
            }

            if (current.ActiveHost == requestedHost)
            {
                throw new WorldSessionConflictException(
                    worldId,
                    "The active host cannot request handoff to itself.");
            }

            var next = current with
            {
                State = SessionState.HandoffRequested,
                RequestedHost = requestedHost,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            if (_sessions.TryUpdate(worldId, next, current))
            {
                return Task.CompletedTask;
            }
        }
    }

    public Task CompleteHandoffAsync(
        WorldId worldId,
        UserIdentity newHost,
        RevisionId committedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newHost);
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            var current = _sessions.GetOrAdd(worldId, CreateAvailable);
            if (current.State != SessionState.HandoffRequested || current.RequestedHost != newHost)
            {
                throw new WorldSessionConflictException(
                    worldId,
                    $"There is no matching handoff request for '{newHost.ExternalId}' after revision '{committedRevision}'.");
            }

            var next = current with
            {
                State = SessionState.Hosting,
                ActiveHost = newHost,
                RequestedHost = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            if (_sessions.TryUpdate(worldId, next, current))
            {
                return Task.CompletedTask;
            }
        }
    }

    public Task ReleaseHostAsync(
        WorldId worldId,
        UserIdentity user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();

        while (true)
        {
            var current = _sessions.GetOrAdd(worldId, CreateAvailable);
            if (current.State == SessionState.Available)
            {
                return Task.CompletedTask;
            }

            if (current.ActiveHost != user)
            {
                throw new WorldSessionConflictException(
                    worldId,
                    $"User '{user.ExternalId}' does not own the host role.");
            }

            var next = new WorldSession(
                worldId,
                SessionState.Available,
                ActiveHost: null,
                UpdatedAt: DateTimeOffset.UtcNow,
                RequestedHost: null);

            if (_sessions.TryUpdate(worldId, next, current))
            {
                return Task.CompletedTask;
            }
        }
    }

    private static WorldSession CreateAvailable(WorldId worldId)
        => new(
            worldId,
            SessionState.Available,
            ActiveHost: null,
            UpdatedAt: DateTimeOffset.UtcNow,
            RequestedHost: null);
}
