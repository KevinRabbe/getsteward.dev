using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;
using SharedWorlds.Core.Sessions;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Maps Steward's host/handoff contract onto one ephemeral peer lobby while fencing lobby creation
/// with both canonical WorldPeerAuthority and a durable per-account authority fence. Steam lobby
/// ownership proves the live host only while a lobby exists; the account fence prevents a former
/// holder's stale local replica from resurrecting authority after a later handoff.
/// </summary>
public sealed class PeerWorldSessionCoordinator : IWorldSessionCoordinator
{
    private readonly IPeerWorldLobby _lobby;
    private readonly IPeerWorldRevisionTransfer _revisionTransfer;
    private readonly IWorldStorage _storage;
    private readonly IPeerAuthorityFenceStore _authorityFences;
    private readonly UserIdentity _localUser;

    public PeerWorldSessionCoordinator(
        IPeerWorldLobby lobby,
        UserIdentity localUser,
        IPeerWorldRevisionTransfer revisionTransfer,
        IWorldStorage storage,
        IPeerAuthorityFenceStore authorityFences)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(localUser);
        ArgumentNullException.ThrowIfNull(revisionTransfer);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(authorityFences);
        _lobby = lobby;
        _localUser = localUser;
        _revisionTransfer = revisionTransfer;
        _storage = storage;
        _authorityFences = authorityFences;
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
        var ownerConfirmed = snapshot.OwnerConfirmed &&
                             await LocalReplicaConfirmsLobbyAuthorityAsync(
                                 snapshot,
                                 cancellationToken);

        var state = !ownerConfirmed
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

        // Both checks occur before Steam lobby creation. A stale former-host replica may still contain
        // old World metadata, but its durable account fence records the later relinquishment and blocks
        // resurrection before any platform-visible authority is created.
        var activeAuthority = await RequireActiveAuthorityAsync(
            worldId,
            user,
            cancellationToken);
        var expectedGeneration = activeAuthority.World.PeerAuthority!.Generation;

        var snapshot = await _lobby.CreateOrGetAsync(
            worldId,
            user,
            cancellationToken);
        EnsureWorld(snapshot, worldId);
        EnsureLobbyGeneration(snapshot, expectedGeneration, worldId);
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

        var authority = await RequireActiveAuthorityAsync(
            worldId,
            _localUser,
            cancellationToken);
        var world = authority.World;
        var expectedGeneration = world.PeerAuthority!.Generation;
        if (!ContainsStableMember(world.Members, requestedHost))
        {
            throw new WorldSessionConflictException(
                worldId,
                "Steward cannot hand writable authority to a participant outside canonical World membership.");
        }

        var current = await RequireOwnedLobbyAsync(worldId, cancellationToken);
        EnsureLobbyGeneration(current, expectedGeneration, worldId);
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
        EnsureLobbyGeneration(updated, expectedGeneration, worldId);
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

        var before = await RequireWorldAuthorityHolderAsync(
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
        var pendingLobby = await RequireMatchingHandoffAsync(
            worldId,
            newHost,
            cancellationToken);
        EnsureLobbyGeneration(
            pendingLobby,
            currentAuthority.Generation,
            worldId);

        var nextGeneration = checked(currentAuthority.Generation + 1);
        var proposedAuthority = new WorldPeerAuthority(
            newHost,
            nextGeneration);
        var proposedWorld = before with { PeerAuthority = proposedAuthority };

        // Relinquishment is deliberately durable before any bytes move. Once this write succeeds the
        // old account can no longer start the World, even from another PC with stale local World data.
        // A retry of the same interrupted handoff reuses the existing Relinquishing fence.
        await EnsureRelinquishingFenceAsync(
            before,
            newHost,
            nextGeneration,
            committedRevision,
            cancellationToken);

        await _revisionTransfer.EnsureAvailableAsync(
            worldId,
            committedRevision,
            newHost,
            cancellationToken);

        // Transfer may take time. The canonical World must still describe the original holder and
        // exact committed state, while the local account fence must still describe this same pending
        // handoff. We intentionally do not require an Active fence anymore because relinquishment is
        // irreversible once begun.
        var stillAuthoritative = await RequireWorldAuthorityHolderAsync(
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

        await RequireMatchingRelinquishingFenceAsync(
            worldId,
            newHost,
            nextGeneration,
            committedRevision,
            cancellationToken);
        var stillPendingLobby = await RequireMatchingHandoffAsync(
            worldId,
            newHost,
            cancellationToken);
        EnsureLobbyGeneration(
            stillPendingLobby,
            currentAuthority.Generation,
            worldId);

        // The target ACK means it has already durably installed generation N+1. From here forward
        // rollback to generation N is forbidden. Publish the same generation locally and record that
        // this account has observed the new holder before touching Steam lobby ownership.
        await _storage.SaveWorldAsync(proposedWorld, cancellationToken);
        await _authorityFences.SaveAsync(
            new PeerAuthorityFence(
                worldId,
                newHost,
                nextGeneration,
                committedRevision,
                PeerAuthorityFenceState.Observed,
                DateTimeOffset.UtcNow),
            cancellationToken);

        var transferred = await _lobby.TransferOwnershipAsync(
            worldId,
            _localUser,
            newHost,
            committedRevision,
            cancellationToken);
        EnsureWorld(transferred, worldId);
        EnsureLobbyGeneration(transferred, nextGeneration, worldId);
        if (!transferred.OwnerConfirmed ||
            !SameUser(transferred.Owner, newHost) ||
            transferred.RequestedHost is not null ||
            transferred.LastCommittedRevision != committedRevision)
        {
            throw new InvalidDataException(
                "The peer lobby did not complete the host transfer correctly.");
        }
    }

    public async Task ReleaseHostAsync(
        WorldId worldId,
        UserIdentity user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        EnsureLocalUser(user);
        var activeAuthority = await RequireActiveAuthorityAsync(
            worldId,
            user,
            cancellationToken);
        var expectedGeneration = activeAuthority.World.PeerAuthority!.Generation;

        var current = await _lobby.GetAsync(worldId, cancellationToken);
        if (current is null)
        {
            return;
        }

        EnsureWorld(current, worldId);
        EnsureLobbyGeneration(current, expectedGeneration, worldId);
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

    private async Task<(World World, PeerAuthorityFence Fence)> RequireActiveAuthorityAsync(
        WorldId worldId,
        UserIdentity expectedHolder,
        CancellationToken cancellationToken)
    {
        var world = await RequireWorldAuthorityHolderAsync(
            worldId,
            expectedHolder,
            cancellationToken);
        var stateRevision = world.CurrentStateRevisionId
            ?? throw new InvalidDataException(
                $"World '{worldId}' has no canonical state revision for peer authority.");
        var fence = await _authorityFences.LoadAsync(worldId, cancellationToken)
            ?? throw new WorldSessionConflictException(
                worldId,
                "The local account has no durable peer-authority fence for this World.");
        var authority = world.PeerAuthority!;
        if (fence.WorldId != worldId ||
            fence.State != PeerAuthorityFenceState.Active ||
            fence.Generation != authority.Generation ||
            fence.StateRevisionId != stateRevision ||
            !SameUser(fence.Holder, expectedHolder))
        {
            throw new WorldSessionConflictException(
                worldId,
                "The local account's durable peer-authority fence does not confirm this replica as the active holder.");
        }

        return (world, fence);
    }

    private async Task<World> RequireWorldAuthorityHolderAsync(
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

    private async Task<bool> LocalReplicaConfirmsLobbyAuthorityAsync(
        PeerWorldLobbySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (snapshot.AuthorityGeneration == 0)
        {
            return false;
        }

        var world = await _storage.LoadWorldAsync(snapshot.WorldId, cancellationToken);
        if (world is null ||
            world.SharingMode != WorldSharingMode.Shared ||
            world.PeerAuthority is not { } authority ||
            authority.Generation != snapshot.AuthorityGeneration ||
            !SameUser(authority.Holder, snapshot.Owner))
        {
            return false;
        }

        if (!SameUser(snapshot.Owner, _localUser))
        {
            return true;
        }

        return await LocalFenceConfirmsActiveAuthorityAsync(
            snapshot.WorldId,
            cancellationToken);
    }

    private async Task EnsureRelinquishingFenceAsync(
        World world,
        UserIdentity newHost,
        ulong nextGeneration,
        RevisionId committedRevision,
        CancellationToken cancellationToken)
    {
        var current = await _authorityFences.LoadAsync(world.Id, cancellationToken);
        var authority = world.PeerAuthority!;
        var currentState = world.CurrentStateRevisionId
            ?? throw new InvalidDataException(
                $"World '{world.Id}' has no canonical state revision for handoff.");

        if (current is not null &&
            current.State == PeerAuthorityFenceState.Relinquishing &&
            current.Generation == nextGeneration &&
            current.StateRevisionId == committedRevision &&
            SameUser(current.Holder, newHost))
        {
            return;
        }

        if (current is null ||
            current.State != PeerAuthorityFenceState.Active ||
            current.Generation != authority.Generation ||
            current.StateRevisionId != currentState ||
            !SameUser(current.Holder, _localUser))
        {
            throw new WorldSessionConflictException(
                world.Id,
                "The local account cannot begin this host handoff because its durable authority fence is missing, stale, or already committed to a different transition.");
        }

        await _authorityFences.SaveAsync(
            new PeerAuthorityFence(
                world.Id,
                newHost,
                nextGeneration,
                committedRevision,
                PeerAuthorityFenceState.Relinquishing,
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private async Task RequireMatchingRelinquishingFenceAsync(
        WorldId worldId,
        UserIdentity newHost,
        ulong generation,
        RevisionId committedRevision,
        CancellationToken cancellationToken)
    {
        var fence = await _authorityFences.LoadAsync(worldId, cancellationToken)
            ?? throw new WorldSessionConflictException(
                worldId,
                "The durable handoff fence disappeared while the World revision was transferring.");
        if (fence.State != PeerAuthorityFenceState.Relinquishing ||
            fence.Generation != generation ||
            fence.StateRevisionId != committedRevision ||
            !SameUser(fence.Holder, newHost))
        {
            throw new WorldSessionConflictException(
                worldId,
                "The durable handoff fence changed while the World revision was transferring.");
        }
    }

    private async Task<bool> LocalFenceConfirmsActiveAuthorityAsync(
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await RequireActiveAuthorityAsync(
                worldId,
                _localUser,
                cancellationToken);
            return true;
        }
        catch (Exception exception) when (
            exception is WorldSessionConflictException or InvalidDataException)
        {
            return false;
        }
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

    private static void EnsureLobbyGeneration(
        PeerWorldLobbySnapshot snapshot,
        ulong expectedGeneration,
        WorldId worldId)
    {
        if (expectedGeneration == 0 ||
            snapshot.AuthorityGeneration == 0 ||
            snapshot.AuthorityGeneration != expectedGeneration)
        {
            throw new WorldSessionConflictException(
                worldId,
                $"Live peer lobby authority generation '{snapshot.AuthorityGeneration}' does not match persistent World generation '{expectedGeneration}'.");
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
