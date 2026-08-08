using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Prevents a stale or mid-handoff host from streaming observer catch-up revisions. Every revision
/// offer must still be owned by the confirmed local lobby owner at the exact live authority generation,
/// and no graceful handoff may be pending.
/// </summary>
public sealed class GenerationBoundPeerWorldObserverSyncExchange :
    IPeerWorldObserverSyncExchange
{
    private readonly IPeerWorldLobby _lobby;
    private readonly UserIdentity _localUser;
    private readonly IPeerWorldObserverSyncExchange _inner;

    public GenerationBoundPeerWorldObserverSyncExchange(
        IPeerWorldLobby lobby,
        UserIdentity localUser,
        IPeerWorldObserverSyncExchange inner)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(localUser);
        ArgumentNullException.ThrowIfNull(inner);
        _lobby = lobby;
        _localUser = localUser;
        _inner = inner;
    }

    public async Task<PeerWorldRevisionReceipt> TransferObserverRevisionAsync(
        UserIdentity targetMember,
        PeerWorldRevisionOffer offer,
        Stream statePayload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetMember);
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(statePayload);
        var authority = offer.World.PeerAuthority
            ?? throw new InvalidDataException(
                "Observer synchronization offer has no persistent World authority.");
        if (!SameUser(authority.Holder, _localUser))
        {
            throw new InvalidOperationException(
                "Only the persistent local authority holder may stream observer catch-up revisions.");
        }

        var live = await _lobby.GetAsync(
            offer.World.Id,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"World '{offer.World.Id}' has no active peer lobby for observer synchronization.");
        if (!live.OwnerConfirmed ||
            live.AuthorityGeneration == 0 ||
            !SameUser(live.Owner, _localUser))
        {
            throw new InvalidOperationException(
                "The live lobby no longer confirms the local user as peer authority holder.");
        }

        if (live.RequestedHost is not null)
        {
            throw new InvalidOperationException(
                "Observer synchronization stops while the active World is changing hosts.");
        }

        if (authority.Generation != live.AuthorityGeneration)
        {
            throw new InvalidOperationException(
                $"Observer synchronization offer generation {authority.Generation} does not match live lobby generation {live.AuthorityGeneration}.");
        }

        return await _inner.TransferObserverRevisionAsync(
            targetMember,
            offer,
            statePayload,
            cancellationToken);
    }

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}
