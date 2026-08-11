using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Sessions;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Adds best-effort member invitation delivery after an inner peer coordinator has already published
/// managed Host Ready. Invitation delivery can never become Host authority and can never roll a Ready
/// host back; failed delivery remains retryable because membership was canonical before this decorator
/// is reached.
/// </summary>
public sealed class PeerWorldMemberInvitationSessionCoordinator : IWorldSessionCoordinator
{
    private readonly IWorldSessionCoordinator _inner;
    private readonly IPeerWorldMemberInvitationService _invitations;
    private readonly UserIdentity _localUser;

    public PeerWorldMemberInvitationSessionCoordinator(
        IWorldSessionCoordinator inner,
        IPeerWorldMemberInvitationService invitations,
        UserIdentity localUser)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(invitations);
        ArgumentNullException.ThrowIfNull(localUser);
        _inner = inner;
        _invitations = invitations;
        _localUser = localUser;
    }

    public Task<WorldSession> GetSessionAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
        => _inner.GetSessionAsync(worldId, cancellationToken);

    public Task<WorldSession> AcquireHostAsync(
        WorldId worldId,
        UserIdentity user,
        CancellationToken cancellationToken = default)
        => _inner.AcquireHostAsync(worldId, user, cancellationToken);

    public Task MarkHostStartingAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
        => _inner.MarkHostStartingAsync(worldId, cancellationToken);

    public async Task MarkHostReadyAsync(
        WorldId worldId,
        ManagedHostEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        // Ready is the authority/joinability transition. Complete it first; invitation delivery is
        // optional UX layered on top of the already-authoritative running host.
        await _inner.MarkHostReadyAsync(
            worldId,
            endpoint,
            cancellationToken);

        try
        {
            _ = await _invitations.InviteCanonicalMembersAsync(
                worldId,
                _localUser,
                cancellationToken);
        }
        catch
        {
            // The host is already Ready. Never report a false Host failure or roll back presence because
            // Steam/provider invitation delivery failed, was cancelled, or local invitation diagnostics
            // were temporarily unavailable. Canonical membership remains persisted for retry.
        }
    }

    public Task EndHostPresenceAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
        => _inner.EndHostPresenceAsync(worldId, cancellationToken);

    public Task RequestHandoffAsync(
        WorldId worldId,
        UserIdentity requestedHost,
        CancellationToken cancellationToken = default)
        => _inner.RequestHandoffAsync(worldId, requestedHost, cancellationToken);

    public Task CompleteHandoffAsync(
        WorldId worldId,
        UserIdentity newHost,
        RevisionId committedRevision,
        CancellationToken cancellationToken = default)
        => _inner.CompleteHandoffAsync(
            worldId,
            newHost,
            committedRevision,
            cancellationToken);

    public Task ReleaseHostAsync(
        WorldId worldId,
        UserIdentity user,
        CancellationToken cancellationToken = default)
        => _inner.ReleaseHostAsync(worldId, user, cancellationToken);
}
