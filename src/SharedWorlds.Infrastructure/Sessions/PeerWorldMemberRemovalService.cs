using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Removes one already-admitted participant from canonical peer World membership while the World is
/// inactive. Live removal is deliberately refused because an active member may have in-flight port-71
/// state transfer or port-72 game traffic; those transports are not treated as revocation authority.
///
/// Once removed offline, future lobby invitations, catch-up requests, game-bridge admission, and host
/// handoff all re-read canonical membership and therefore reject the stale participant.
/// </summary>
public sealed class PeerWorldMemberRemovalService
{
    private readonly IWorldStorage _storage;
    private readonly IPeerAuthorityFenceStore _authorityFences;
    private readonly IPeerWorldLobby _lobby;

    public PeerWorldMemberRemovalService(
        IWorldStorage storage,
        IPeerAuthorityFenceStore authorityFences,
        IPeerWorldLobby lobby)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(authorityFences);
        ArgumentNullException.ThrowIfNull(lobby);
        _storage = storage;
        _authorityFences = authorityFences;
        _lobby = lobby;
    }

    public async Task<World> RemoveMemberAsync(
        WorldId worldId,
        UserIdentity localHolder,
        UserIdentity member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localHolder);
        ArgumentNullException.ThrowIfNull(member);
        if (SameUser(localHolder, member))
        {
            throw new InvalidOperationException(
                "The persistent peer authority holder cannot remove itself. Transfer authority or close the World through a dedicated owner workflow first.");
        }

        var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new InvalidDataException(
                $"Cannot remove a peer member from missing World '{worldId}'.");
        if (world.SharingMode != WorldSharingMode.Shared)
        {
            throw new InvalidOperationException(
                $"World '{worldId}' is local-only and has no peer access list to revoke.");
        }

        var authority = world.PeerAuthority
            ?? throw new InvalidOperationException(
                $"World '{worldId}' has not been migrated to persistent peer authority.");
        if (authority.Generation == 0 ||
            !SameUser(authority.Holder, localHolder) ||
            !ContainsStableMember(world.Members, localHolder))
        {
            throw new UnauthorizedAccessException(
                $"Identity '{localHolder.ExternalId}' is not the canonical peer authority holder for World '{worldId}'.");
        }

        var stateRevision = world.CurrentStateRevisionId
            ?? throw new InvalidDataException(
                $"World '{worldId}' has no canonical state revision for membership fencing.");
        var fence = await _authorityFences.LoadAsync(
            worldId,
            cancellationToken)
            ?? throw new UnauthorizedAccessException(
                $"Local authority account has no durable fence for World '{worldId}'.");
        if (fence.State != PeerAuthorityFenceState.Active ||
            fence.Generation != authority.Generation ||
            fence.StateRevisionId != stateRevision ||
            !SameUser(fence.Holder, localHolder))
        {
            throw new UnauthorizedAccessException(
                $"Local durable authority fence does not permit membership changes for World '{worldId}'.");
        }

        if (!ContainsStableMember(world.Members, member))
        {
            return world;
        }

        var liveLobby = await _lobby.GetAsync(worldId, cancellationToken);
        if (liveLobby is not null)
        {
            throw new InvalidOperationException(
                "Stop hosting this World before removing access. Steward does not perform partial live kicks while peer state/game traffic may still be active.");
        }

        var updatedMembers = world.Members
            .Where(existing => !SameUser(existing, member))
            .ToArray();
        if (updatedMembers.Length == 0 ||
            !ContainsStableMember(updatedMembers, localHolder))
        {
            throw new InvalidDataException(
                $"Removing '{member.ExternalId}' would leave World '{worldId}' without its persistent authority holder membership.");
        }

        var updated = world with { Members = updatedMembers };
        await _storage.SaveWorldAsync(updated, cancellationToken);

        var verified = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new IOException(
                $"World '{worldId}' disappeared after peer membership removal.");
        if (ContainsStableMember(verified.Members, member) ||
            !ContainsStableMember(verified.Members, localHolder) ||
            verified.PeerAuthority is not { } verifiedAuthority ||
            verifiedAuthority.Generation != authority.Generation ||
            !SameUser(verifiedAuthority.Holder, localHolder) ||
            verified.CurrentStateRevisionId != stateRevision)
        {
            throw new IOException(
                $"World '{worldId}' did not persist the exact peer membership removal under the current authority generation.");
        }

        return verified;
    }

    private static bool ContainsStableMember(
        IReadOnlyList<UserIdentity> members,
        UserIdentity expected)
        => members.Any(member => SameUser(member, expected));

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}
