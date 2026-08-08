using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;
using SharedWorlds.Core.Sessions;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Maps Steward's existing host/handoff contract onto one ephemeral peer lobby.
/// Durable World state remains owned by IWorldStorage.
/// </summary>
public sealed class PeerWorldSessionCoordinator : IWorldSessionCoordinator
{
    private readonly IPeerWorldLobby _lobby;
    private readonly UserIdentity _localUser;

    public PeerWorldSessionCoordinator(IPeerWorldLobby lobby, UserIdentity localUser)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(localUser);
        _lobby = lobby;
        _localUser = localUser;
    }

    public async Task<WorldSession> GetSessionAsync(WorldId worldId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _lobby.GetAsync(worldId, cancellationToken);
        if (snapshot is null)
        {
            return new WorldSession(worldId, SessionState.Available, null, DateTimeOffset.UtcNow);
        }

        EnsureWorld(snapshot, worldId);
        return new WorldSession(
            worldId,
            snapshot.RequestedHost is null ? SessionState.Hosting : SessionState.HandoffRequested,
            snapshot.Owner,
            snapshot.UpdatedAt,
            snapshot.RequestedHost);
    }

    public async Task<WorldSession> AcquireHostAsync(
        WorldId worldId,
        UserIdentity user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        EnsureLocalUser(user);

        var snapshot = await _lobby.CreateOrGetAsync(worldId, user, cancellationToken);
        EnsureWorld(snapshot, worldId);
        if (snapshot.Owner != user)
        {
            throw new WorldSessionConflictException(worldId, "Another participant already hosts this World.");
        }

        if (snapshot.RequestedHost is not null)
        {
            throw new WorldSessionConflictException(worldId, "A host handoff is already in progress.");
        }

        return new WorldSession(worldId, SessionState.Hosting, user, snapshot.UpdatedAt);
    }

    public async Task RequestHandoffAsync(
        WorldId worldId,
        UserIdentity requestedHost,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedHost);
        if (requestedHost == _localUser)
        {
            throw new WorldSessionConflictException(worldId, "The active host cannot hand the World to itself.");
        }

        var current = await RequireOwnedLobbyAsync(worldId, cancellationToken);
        if (current.RequestedHost is not null && current.RequestedHost != requestedHost)
        {
            throw new WorldSessionConflictException(worldId, "A different host handoff is already in progress.");
        }

        var updated = await _lobby.RequestHandoffAsync(
            worldId,
            _localUser,
            requestedHost,
            cancellationToken);
        EnsureWorld(updated, worldId);
        if (updated.Owner != _localUser || updated.RequestedHost != requestedHost)
        {
            throw new InvalidDataException("The peer lobby did not preserve the requested host handoff.");
        }
    }

    public async Task CompleteHandoffAsync(
        WorldId worldId,
        UserIdentity newHost,
        RevisionId committedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newHost);
        var current = await RequireOwnedLobbyAsync(worldId, cancellationToken);
        if (current.RequestedHost != newHost)
        {
            throw new WorldSessionConflictException(worldId, "There is no matching host handoff request.");
        }

        var transferred = await _lobby.TransferOwnershipAsync(
            worldId,
            _localUser,
            newHost,
            committedRevision,
            cancellationToken);
        EnsureWorld(transferred, worldId);
        if (transferred.Owner != newHost ||
            transferred.RequestedHost is not null ||
            transferred.LastCommittedRevision != committedRevision)
        {
            throw new InvalidDataException("The peer lobby did not complete the host transfer correctly.");
        }
    }

    public async Task ReleaseHostAsync(
        WorldId worldId,
        UserIdentity user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        EnsureLocalUser(user);
        var current = await _lobby.GetAsync(worldId, cancellationToken);
        if (current is null)
        {
            return;
        }

        EnsureWorld(current, worldId);
        if (current.Owner != user)
        {
            throw new WorldSessionConflictException(worldId, "Only the current host may close the peer lobby.");
        }

        if (current.RequestedHost is not null)
        {
            throw new WorldSessionConflictException(worldId, "The pending host handoff must resolve before leaving.");
        }

        await _lobby.LeaveAsync(worldId, user, cancellationToken);
    }

    private async Task<PeerWorldLobbySnapshot> RequireOwnedLobbyAsync(
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        var current = await _lobby.GetAsync(worldId, cancellationToken)
            ?? throw new WorldSessionConflictException(worldId, "There is no active host lobby for this World.");
        EnsureWorld(current, worldId);
        if (current.Owner != _localUser)
        {
            throw new WorldSessionConflictException(worldId, "Only the current host may change host ownership.");
        }

        return current;
    }

    private void EnsureLocalUser(UserIdentity user)
    {
        if (user != _localUser)
        {
            throw new InvalidOperationException("Peer session coordination can act only for the local user.");
        }
    }

    private static void EnsureWorld(PeerWorldLobbySnapshot snapshot, WorldId expectedWorldId)
    {
        if (snapshot.WorldId != expectedWorldId)
        {
            throw new InvalidDataException("The peer lobby returned a different Steward World.");
        }
    }
}
