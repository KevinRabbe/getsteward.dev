using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Provider transport boundary for delivering an invitation to one already-authorized canonical World
/// member. Transport presence is never membership authority: callers must validate the canonical World
/// before invoking this boundary.
/// </summary>
public interface IPeerWorldMemberInvitationTransport
{
    bool Supports(UserIdentity member);

    Task InviteAsync(
        WorldId worldId,
        UserIdentity expectedHolder,
        ulong authorityGeneration,
        UserIdentity member,
        CancellationToken cancellationToken = default);
}

public sealed record PeerWorldMemberInvitationResult(
    int CanonicalRemoteMembers,
    int Delivered,
    int Unsupported,
    int Failed);

public interface IPeerWorldMemberInvitationService
{
    Task<PeerWorldMemberInvitationResult> InviteCanonicalMembersAsync(
        WorldId worldId,
        UserIdentity localHolder,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Selects invitation recipients exclusively from canonical World membership. A platform invitation is
/// only a delivery mechanism: membership must already be persisted, the local caller must still be the
/// persistent holder, and the account fence must still be Active at the exact canonical head.
/// Individual transport failures are reported but never roll canonical membership back.
/// </summary>
public sealed class PeerWorldMemberInvitationService : IPeerWorldMemberInvitationService
{
    private readonly IWorldStorage _storage;
    private readonly IPeerAuthorityFenceStore _authorityFences;
    private readonly IPeerWorldMemberInvitationTransport _transport;

    public PeerWorldMemberInvitationService(
        IWorldStorage storage,
        IPeerAuthorityFenceStore authorityFences,
        IPeerWorldMemberInvitationTransport transport)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(authorityFences);
        ArgumentNullException.ThrowIfNull(transport);
        _storage = storage;
        _authorityFences = authorityFences;
        _transport = transport;
    }

    public async Task<PeerWorldMemberInvitationResult> InviteCanonicalMembersAsync(
        WorldId worldId,
        UserIdentity localHolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(localHolder);
        var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new InvalidDataException(
                $"Cannot invite members for missing World '{worldId}'.");
        if (world.SharingMode != WorldSharingMode.Shared)
        {
            throw new InvalidOperationException(
                $"World '{worldId}' is local-only and has no peer lobby invitations.");
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
                $"World '{worldId}' has no canonical state revision for invitation fencing.");
        var fence = await _authorityFences.LoadAsync(worldId, cancellationToken)
            ?? throw new UnauthorizedAccessException(
                $"Local authority account has no durable fence for World '{worldId}'.");
        if (fence.State != PeerAuthorityFenceState.Active ||
            fence.Generation != authority.Generation ||
            fence.StateRevisionId != stateRevision ||
            !SameUser(fence.Holder, localHolder))
        {
            throw new UnauthorizedAccessException(
                $"Local durable authority fence does not permit invitations for World '{worldId}'.");
        }

        var delivered = 0;
        var unsupported = 0;
        var failed = 0;
        var canonicalRemoteMembers = 0;
        foreach (var member in world.Members)
        {
            if (SameUser(member, localHolder))
            {
                continue;
            }

            canonicalRemoteMembers++;
            if (!_transport.Supports(member))
            {
                unsupported++;
                continue;
            }

            try
            {
                await _transport.InviteAsync(
                    worldId,
                    localHolder,
                    authority.Generation,
                    member,
                    cancellationToken);
                delivered++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Membership is already canonical. Platform delivery is intentionally retryable and
                // cannot roll back or partially rewrite World access merely because one invite failed.
                failed++;
            }
        }

        return new PeerWorldMemberInvitationResult(
            canonicalRemoteMembers,
            delivered,
            unsupported,
            failed);
    }

    private static bool ContainsStableMember(
        IReadOnlyList<UserIdentity> members,
        UserIdentity expected)
        => members.Any(member => SameUser(member, expected));

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}
