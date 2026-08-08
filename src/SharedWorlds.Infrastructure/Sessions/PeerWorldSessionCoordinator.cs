using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;
using SharedWorlds.Core.Sessions;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Maps Steward's host/handoff contract onto one ephemeral peer lobby while fencing lobby creation
/// with persistent WorldPeerAuthority. Steam ownership proves the active host only while a lobby
/// exists; the persisted holder is the only participant allowed to create the next lobby later.
/// </summary>
public sealed class PeerWorldSessionCoordinator : IWorldSessionCoordinator
{
    private readonly IPeerWorldLobby _lobby;
    private readonly IPeerWorldRevisionTransfer _revisionTransfer;
    private readonly IWorldStorage _storage;
    private readonly UserIdentity _localUser;

    public PeerWorldSessionCoordinator(
        IPeerWorldLobby lobby,
        UserIdentity localUser,
        IPeerWorldRevisionTransfer revisionTransfer,
        IWorldStorage storage)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(localUser);
        ArgumentNullException.ThrowIfNull(revisionTransfer);
        ArgumentNullException.ThrowIfNull(storage);
        _lobby = lobby;
        _localUser = localUser;
        _revisionTransfer = revisionTransfer;
        _storage = storage;
    }

    public async Task<WorldSession> GetSessionAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _lobby.GetAsync(worldId, cancellationToken);
        if (snapshot is null)
        {
            return new WorldSession(
                worldId,
                SessionState.Available,
                null,
                DateTimeOffset.UtcNow);
        }

        EnsureWorld(snapshot, worldId);
        var state = !snapshot.OwnerConfirmed
            ? SessionState.RecoveryPending
            : snapshot.RequestedHost is null
                ? SessionState.Hosting
                : SessionState.HandoffRequested;
        return new WorldSession(
            worldId,
            state,
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

        // This check occurs before Steam lobby creation. A stale member replica therefore cannot
        // become writable merely because the previous lobby is inactive or unreachable.
        _ = await RequirePersistentAuthorityAsync(
            worldId,
            user,
            cancellationToken);

        var snapshot = await _lobby.CreateOrGetAsync(
            worldId,
            user,
            cancellationToken);
        EnsureWorld(snapshot, worldId);
        if (!snapshot.OwnerConfirmed)
        {
            throw new WorldSessionConflictException(
                worldId,
                "The platform selected a lobby owner that Steward has not confirmed against persistent World authority.");
        }

        if (!SameUser(snapshot.Owner, user))
        {
            throw new WorldSessionConflictException(
                worldId,
                "Another participant already hosts this World.");
        }

        if (snapshot.RequestedHost is not null)
        {
            throw new WorldSessionConflictException(
                worldId,
                "A host handoff is already in progress.");
        }

        return new WorldSession(
            worldId,
            SessionState.Hosting,
            user,
            snapshot.UpdatedAt);
    }

    public async Task RequestHandoffAsync(
        WorldId worldId,
        UserIdentity requestedHost,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedHost);
        if (SameUser(requestedHost, _localUser))
        {
            throw new WorldSessionConflictException(
                worldId,
                "The active host cannot hand the World to itself.");
        }

        var world = await RequirePersistentAuthorityAsync(
            worldId,
            _localUser,
            cancellationToken);
        if (!ContainsStableMember(world.Members, requestedHost))
        {
            throw new WorldSessionConflictException(
                worldId,
                "Steward cannot hand writable authority to a participant outside canonical World membership.");
        }

        var current = await RequireOwnedLobbyAsync(worldId, cancellationToken);
        if (current.RequestedHost is not null &&
            !SameUser(current.RequestedHost, requestedHost))
        {
            throw new WorldSessionConflictException(
                worldId,
                "A different host handoff is already in progress.");
        }

        var updated = await _lobby.RequestHandoffAsync(
            worldId,
            _localUser,
            requestedHost,
            cancellationToken);
        EnsureWorld(updated, worldId);
        if (!updated.OwnerConfirmed ||
            !SameUser(updated.Owner, _localUser) ||
            updated.RequestedHost is null ||
            !SameUser(updated.RequestedHost, requestedHost))
        {
            throw new InvalidDataException(
                "The peer lobby did not preserve the requested host handoff.");
        }
    }

    public async Task CompleteHandoffAsync(
        WorldId worldId,
        UserIdentity newHost,
        RevisionId committedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newHost);
        _ = await RequireMatchingHandoffAsync(
            worldId,
            newHost,
            cancellationToken);

        var before = await RequirePersistentAuthorityAsync(
            worldId,
            _localUser,
            cancellationToken);
        if (before.CurrentStateRevisionId != committedRevision)
        {
            throw new WorldSessionConflictException(
                worldId,
                "Persistent World metadata is not on the committed revision required for host handoff.");
        }

        var currentAuthority = before.PeerAuthority!;
        var proposedAuthority = new WorldPeerAuthority(
            newHost,
            checked(currentAuthority.Generation + 1));
        var proposedWorld = before with { PeerAuthority = proposedAuthority };

        // The revision-transfer service publishes the exact committed state plus the prospective
        // authority generation to the target before any local or Steam authority moves.
        await _revisionTransfer.EnsureAvailableAsync(
            worldId,
            committedRevision,
            newHost,
            cancellationToken);

        // Transfer may take time. Revalidate both persistent and live authority before changing any
        // local authority fence.
        var stillAuthoritative = await RequirePersistentAuthorityAsync(
            worldId,
            _localUser,
            cancellationToken);
        if (stillAuthoritative.PeerAuthority!.Generation != currentAuthority.Generation ||
            stillAuthoritative.CurrentStateRevisionId != committedRevision)
        {
            throw new WorldSessionConflictException(
                worldId,
                "Persistent World authority changed while Steward was transferring the committed revision.");
        }

        _ = await RequireMatchingHandoffAsync(
            worldId,
            newHost,
            cancellationToken);

        // Persist the new holder locally before Steam ownership changes. If this process disappears
        // after this write but before SetLobbyOwner, the old host cannot later restart from a stale
        // local authority claim. A confirmed Steam rejection may safely roll this write back.
        await _storage.SaveWorldAsync(proposedWorld, cancellationToken);

        try
        {
            var transferred = await _lobby.TransferOwnershipAsync(
                worldId,
                _localUser,
                newHost,
                committedRevision,
                cancellationToken);
            EnsureWorld(transferred, worldId);
            if (!transferred.OwnerConfirmed ||
                !SameUser(transferred.Owner, newHost) ||
                transferred.RequestedHost is not null ||
                transferred.LastCommittedRevision != committedRevision)
            {
                throw new InvalidDataException(
                    "The peer lobby did not complete the host transfer correctly.");
            }
        }
        catch
        {
            // Roll back only when Steam still proves the old local user is the confirmed owner.
            // Ambiguous/automatic owner changes stay fail-closed with the prospective new holder.
            var observed = await _lobby.GetAsync(worldId, CancellationToken.None);
            if (observed is not null &&
                observed.OwnerConfirmed &&
                SameUser(observed.Owner, _localUser))
            {
                try
                {
                    await _storage.SaveWorldAsync(before, CancellationToken.None);
                }
                catch
                {
                    // Leaving the prospective authority fence is safer than restoring the old holder
                    // without proving that rollback was durably written.
                }
            }

            throw;
        }
    }

    public async Task ReleaseHostAsync(
        WorldId worldId,
        UserIdentity user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        EnsureLocalUser(user);
        _ = await RequirePersistentAuthorityAsync(
            worldId,
            user,
            cancellationToken);

        var current = await _lobby.GetAsync(worldId, cancellationToken);
        if (current is null)
        {
            return;
        }

        EnsureWorld(current, worldId);
        if (!current.OwnerConfirmed)
        {
            throw new WorldSessionConflictException(
                worldId,
                "The current platform owner is recovery-pending and is not confirmed as the Steward host.");
        }

        if (!SameUser(current.Owner, user))
        {
            throw new WorldSessionConflictException(
                worldId,
                "Only the current host may close the peer lobby.");
        }

        if (current.RequestedHost is not null)
        {
            throw new WorldSessionConflictException(
                worldId,
                "The pending host handoff must resolve before leaving.");
        }

        await _lobby.LeaveAsync(worldId, user, cancellationToken);
    }

    private async Task<World> RequirePersistentAuthorityAsync(
        WorldId worldId,
        UserIdentity expectedHolder,
        CancellationToken cancellationToken)
    {
        var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new WorldSessionConflictException(
                worldId,
                "The local canonical World metadata is missing.");
        if (world.SharingMode != WorldSharingMode.Shared)
        {
            throw new WorldSessionConflictException(
                worldId,
                "Only a shared World can use peer host authority.");
        }

        var authority = world.PeerAuthority
            ?? throw new WorldSessionConflictException(
                worldId,
                "This shared World has not been migrated to persistent peer authority.");
        if (authority.Generation == 0)
        {
            throw new InvalidDataException(
                $"World '{worldId}' has invalid zero peer-authority generation.");
        }

        if (!SameUser(authority.Holder, expectedHolder))
        {
            throw new WorldSessionConflictException(
                worldId,
                $"Persistent World authority belongs to '{authority.Holder.ExternalId}', not the local participant.");
        }

        if (!ContainsStableMember(world.Members, authority.Holder))
        {
            throw new InvalidDataException(
                $"World '{worldId}' persistent authority holder is not a canonical World member.");
        }

        return world;
    }

    private async Task<PeerWorldLobbySnapshot> RequireMatchingHandoffAsync(
        WorldId worldId,
        UserIdentity requestedHost,
        CancellationToken cancellationToken)
    {
        var current = await RequireOwnedLobbyAsync(worldId, cancellationToken);
        if (current.RequestedHost is null ||
            !SameUser(current.RequestedHost, requestedHost))
        {
            throw new WorldSessionConflictException(
                worldId,
                "There is no matching host handoff request.");
        }

        return current;
    }

    private async Task<PeerWorldLobbySnapshot> RequireOwnedLobbyAsync(
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        var current = await _lobby.GetAsync(worldId, cancellationToken)
            ?? throw new WorldSessionConflictException(
                worldId,
                "There is no active host lobby for this World.");
        EnsureWorld(current, worldId);
        if (!current.OwnerConfirmed)
        {
            throw new WorldSessionConflictException(
                worldId,
                "The current platform owner is recovery-pending and cannot change Steward host authority.");
        }

        if (!SameUser(current.Owner, _localUser))
        {
            throw new WorldSessionConflictException(
                worldId,
                "Only the current host may change host ownership.");
        }

        return current;
    }

    private void EnsureLocalUser(UserIdentity user)
    {
        if (!SameUser(user, _localUser))
        {
            throw new InvalidOperationException(
                "Peer session coordination can act only for the local user.");
        }
    }

    private static bool ContainsStableMember(
        IReadOnlyList<UserIdentity> members,
        UserIdentity expected)
        => members.Any(member => SameUser(member, expected));

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);

    private static void EnsureWorld(
        PeerWorldLobbySnapshot snapshot,
        WorldId expectedWorldId)
    {
        if (snapshot.WorldId != expectedWorldId)
        {
            throw new InvalidDataException(
                "The peer lobby returned a different Steward World.");
        }
    }
}
