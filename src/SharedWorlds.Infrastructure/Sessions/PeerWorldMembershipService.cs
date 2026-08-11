using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Canonically admits a participant to a shared peer World. Steam lobby presence is never treated as
/// membership authority: only the persistent holder with an exact Active account fence may mutate the
/// member list, and the World is saved before any later platform invite/bootstrap step. When composed
/// for a live peer runtime, the same mutation gate used by handoff/removal prevents membership writes
/// from being overwritten by a concurrent authority transfer.
/// </summary>
public sealed class PeerWorldMembershipService
{
    public const int MaximumPeerMembers = 250;

    private readonly IWorldStorage _storage;
    private readonly IPeerAuthorityFenceStore _authorityFences;
    private readonly PeerWorldLiveMemberRevocationRegistry? _liveRevocations;
    private readonly PeerWorldLiveAuthorityMutationGate? _liveAuthorityMutations;

    public PeerWorldMembershipService(
        IWorldStorage storage,
        IPeerAuthorityFenceStore authorityFences,
        PeerWorldLiveMemberRevocationRegistry? liveRevocations = null,
        PeerWorldLiveAuthorityMutationGate? liveAuthorityMutations = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(authorityFences);
        _storage = storage;
        _authorityFences = authorityFences;
        _liveRevocations = liveRevocations;
        _liveAuthorityMutations = liveAuthorityMutations;
    }

    public async Task<World> AddMemberAsync(
        WorldId worldId,
        UserIdentity localHolder,
        UserIdentity newMember,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localHolder);
        ArgumentNullException.ThrowIfNull(newMember);
        using var mutationLease = _liveAuthorityMutations is null
            ? null
            : await _liveAuthorityMutations.EnterAsync(cancellationToken);

        var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new InvalidDataException(
                $"Cannot add a peer member to missing World '{worldId}'.");
        if (world.SharingMode != WorldSharingMode.Shared)
        {
            throw new InvalidOperationException(
                $"World '{worldId}' is local-only and cannot admit peer members.");
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

        if (ContainsStableMember(world.Members, newMember))
        {
            // A holder-controlled Add person is also the explicit re-admission boundary for a member
            // whose previous live revocation is still remembered in this process.
            _liveRevocations?.Restore(worldId, authority.Generation, newMember);
            return world;
        }

        if (world.Members.Count >= MaximumPeerMembers)
        {
            throw new InvalidOperationException(
                $"World '{worldId}' already has Steward's {MaximumPeerMembers}-member peer safety limit.");
        }

        var updatedMembers = world.Members
            .Concat([newMember])
            .ToArray();
        var updated = world with { Members = updatedMembers };
        await _storage.SaveWorldAsync(updated, cancellationToken);

        // Restore only after canonical membership persistence. A failed World write therefore cannot
        // accidentally reopen a member that remains canonically removed.
        _liveRevocations?.Restore(worldId, authority.Generation, newMember);
        return updated;
    }

    private static bool ContainsStableMember(
        IReadOnlyList<UserIdentity> members,
        UserIdentity expected)
        => members.Any(member => SameUser(member, expected));

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}
