using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Sessions;

public sealed record PeerWorldLeaveRequest(
    WorldId WorldId,
    ulong AuthorityGeneration);

public sealed record PeerWorldLeaveResult(
    WorldId WorldId,
    ulong AuthorityGeneration,
    RevisionId CurrentStateRevisionId);

/// <summary>
/// Client-side control boundary for a non-holder member asking the confirmed active holder to remove
/// that authenticated member canonically. The request deliberately carries no member identity: the
/// Steam transport supplies it, preventing one participant from asking to remove another participant.
/// </summary>
public interface IPeerWorldLeaveRequestClient
{
    Task<PeerWorldLeaveResult> RequestLeaveAsync(
        UserIdentity confirmedHost,
        PeerWorldLeaveRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Holder-side Leave World router. It authenticates the request against the exact current peer
/// authority/lobby generation, then invokes the same safe member-removal transaction used by Manage
/// access. The leave control connection itself is intentionally not linked to the member revocation
/// token: it must survive its own revocation long enough to return the post-persistence acknowledgement.
/// All other cancellation-scoped port-71 work and targeted port-72 sessions are revoked by the removal
/// transaction before canonical membership is changed.
/// </summary>
public sealed class PeerWorldLeaveRequestRouter
{
    private readonly IWorldStorage _storage;
    private readonly IPeerWorldLobby _lobby;
    private readonly PeerWorldMemberRemovalService _memberRemoval;
    private readonly UserIdentity _localHolder;

    public PeerWorldLeaveRequestRouter(
        IWorldStorage storage,
        IPeerWorldLobby lobby,
        PeerWorldMemberRemovalService memberRemoval,
        UserIdentity localHolder)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(memberRemoval);
        ArgumentNullException.ThrowIfNull(localHolder);
        _storage = storage;
        _lobby = lobby;
        _memberRemoval = memberRemoval;
        _localHolder = localHolder;
    }

    public async Task<PeerWorldLeaveResult> HandleAsync(
        UserIdentity authenticatedRemoteUser,
        PeerWorldLeaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authenticatedRemoteUser);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.WorldId.Value == Guid.Empty || request.AuthorityGeneration == 0)
        {
            throw new InvalidDataException(
                "Peer Leave World request requires a World ID and nonzero authority generation.");
        }

        if (SameUser(authenticatedRemoteUser, _localHolder))
        {
            throw new InvalidOperationException(
                "The persistent authority holder cannot leave through the member Leave World request. Hand off host authority first.");
        }

        var before = await _storage.LoadWorldAsync(
            request.WorldId,
            cancellationToken)
            ?? throw new InvalidDataException(
                $"Cannot process Leave World for missing canonical World '{request.WorldId}'.");
        if (before.SharingMode != WorldSharingMode.Shared ||
            before.PeerAuthority is not { } authority ||
            authority.Generation != request.AuthorityGeneration ||
            !SameUser(authority.Holder, _localHolder) ||
            !ContainsStableMember(before.Members, _localHolder) ||
            !ContainsStableMember(before.Members, authenticatedRemoteUser))
        {
            throw new InvalidOperationException(
                "Leave World request does not match current canonical membership and peer authority generation.");
        }

        var liveLobby = await _lobby.GetAsync(request.WorldId, cancellationToken)
            ?? throw new InvalidOperationException(
                "Leave World requires the current authority holder to be actively hosting this World.");
        if (!liveLobby.OwnerConfirmed ||
            liveLobby.AuthorityGeneration != request.AuthorityGeneration ||
            !SameUser(liveLobby.Owner, _localHolder) ||
            liveLobby.RequestedHost is not null)
        {
            throw new InvalidOperationException(
                "Leave World requires the confirmed current host/generation and is unavailable during host handoff.");
        }

        var updated = await _memberRemoval.RemoveMemberAtGenerationAsync(
            request.WorldId,
            _localHolder,
            authenticatedRemoteUser,
            request.AuthorityGeneration,
            cancellationToken);
        if (ContainsStableMember(updated.Members, authenticatedRemoteUser) ||
            updated.PeerAuthority is not { } updatedAuthority ||
            updatedAuthority.Generation != request.AuthorityGeneration ||
            !SameUser(updatedAuthority.Holder, _localHolder) ||
            updated.CurrentStateRevisionId is not { } currentStateRevisionId)
        {
            throw new IOException(
                "Canonical Leave World removal did not produce the exact expected holder/generation/member state.");
        }

        return new PeerWorldLeaveResult(
            request.WorldId,
            request.AuthorityGeneration,
            currentStateRevisionId);
    }

    private static bool ContainsStableMember(
        IReadOnlyList<UserIdentity> members,
        UserIdentity expected)
        => members.Any(member => SameUser(member, expected));

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}
