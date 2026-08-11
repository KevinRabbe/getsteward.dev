using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Removes one already-admitted participant from canonical peer World membership. Offline removal needs
/// only canonical authority. During a live Host, removal additionally requires the process-local live
/// revocation registry and the shared handoff/removal mutation gate: the exact member/generation is
/// denied and existing cancellation-scoped traffic is stopped before canonical membership is changed.
/// Long-lived game traffic is closed by the concrete port-72 bridge subscriber to the same revocation.
/// </summary>
public sealed class PeerWorldMemberRemovalService
{
    private readonly IWorldStorage _storage;
    private readonly IPeerAuthorityFenceStore _authorityFences;
    private readonly IPeerWorldLobby _lobby;
    private readonly PeerWorldLiveMemberRevocationRegistry? _liveRevocations;
    private readonly PeerWorldLiveAuthorityMutationGate? _liveAuthorityMutations;

    public PeerWorldMemberRemovalService(
        IWorldStorage storage,
        IPeerAuthorityFenceStore authorityFences,
        IPeerWorldLobby lobby,
        PeerWorldLiveMemberRevocationRegistry? liveRevocations = null,
        PeerWorldLiveAuthorityMutationGate? liveAuthorityMutations = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(authorityFences);
        ArgumentNullException.ThrowIfNull(lobby);
        _storage = storage;
        _authorityFences = authorityFences;
        _lobby = lobby;
        _liveRevocations = liveRevocations;
        _liveAuthorityMutations = liveAuthorityMutations;
    }

    public Task<World> RemoveMemberAsync(
        WorldId worldId,
        UserIdentity localHolder,
        UserIdentity member,
        CancellationToken cancellationToken = default)
        => RemoveMemberCoreAsync(
            worldId,
            localHolder,
            member,
            expectedAuthorityGeneration: null,
            cancellationToken);

    /// <summary>
    /// Removes a member only if the exact authority generation observed by an authenticated remote
    /// control request is still current. This prevents a delayed Leave World request from mutating a
    /// later authority generation after host handoff/recovery/re-admission.
    /// </summary>
    public Task<World> RemoveMemberAtGenerationAsync(
        WorldId worldId,
        UserIdentity localHolder,
        UserIdentity member,
        ulong expectedAuthorityGeneration,
        CancellationToken cancellationToken = default)
    {
        if (expectedAuthorityGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedAuthorityGeneration),
                "Exact-generation member removal requires a nonzero peer authority generation.");
        }

        return RemoveMemberCoreAsync(
            worldId,
            localHolder,
            member,
            expectedAuthorityGeneration,
            cancellationToken);
    }

    private async Task<World> RemoveMemberCoreAsync(
        WorldId worldId,
        UserIdentity localHolder,
        UserIdentity member,
        ulong? expectedAuthorityGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(localHolder);
        ArgumentNullException.ThrowIfNull(member);
        if (SameUser(localHolder, member))
        {
            throw new InvalidOperationException(
                "The persistent peer authority holder cannot remove itself. Transfer authority or close the World through a dedicated owner workflow first.");
        }

        using var mutationLease = _liveAuthorityMutations is null
            ? null
            : await _liveAuthorityMutations.EnterAsync(cancellationToken);

        // Load and validate only after entering the shared live mutation boundary. RequestHandoff uses
        // the same gate, so either this removal observes RequestedHost and refuses, or handoff observes
        // the already-persisted removal and cannot select that participant as the next holder.
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
            (expectedAuthorityGeneration is { } expectedGeneration &&
             authority.Generation != expectedGeneration) ||
            !SameUser(authority.Holder, localHolder) ||
            !ContainsStableMember(world.Members, localHolder))
        {
            throw new UnauthorizedAccessException(
                expectedAuthorityGeneration is null
                    ? $"Identity '{localHolder.ExternalId}' is not the canonical peer authority holder for World '{worldId}'."
                    : $"Identity '{localHolder.ExternalId}' does not hold the requested exact authority generation for World '{worldId}'.");
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
            if (_liveRevocations is null || _liveAuthorityMutations is null)
            {
                throw new InvalidOperationException(
                    "Stop hosting this World before removing access. This Steward runtime does not provide the complete live member revocation boundary.");
            }

            if (liveLobby.WorldId != worldId ||
                !liveLobby.OwnerConfirmed ||
                liveLobby.AuthorityGeneration != authority.Generation ||
                !SameUser(liveLobby.Owner, localHolder))
            {
                throw new InvalidOperationException(
                    "Live member removal requires this Steward instance to be the confirmed lobby owner at the exact persistent authority generation.");
            }

            if (liveLobby.RequestedHost is not null)
            {
                throw new InvalidOperationException(
                    "Remove access cannot run while a host handoff is in progress.");
            }

            // Fail closed before canonical mutation. Revoke cancels port-71 work and notifies the
            // concrete port-72 bridge before this method can remove the member from persistent state.
            // If the later save is ambiguous/fails, the deny fence deliberately remains active.
            _liveRevocations.Revoke(
                worldId,
                authority.Generation,
                member);
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
