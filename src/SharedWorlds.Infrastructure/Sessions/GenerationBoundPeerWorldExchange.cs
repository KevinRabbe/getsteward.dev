using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Defense-in-depth wrapper that binds P2P World transfer offers to the currently attached live lobby
/// generation before bytes leave the source. Bootstrap carries the current generation; handoff carries
/// exactly the prospective next generation while the live lobby still represents the outgoing host.
/// </summary>
public sealed class GenerationBoundPeerWorldExchange :
    IPeerWorldRevisionExchange,
    IPeerWorldBootstrapExchange
{
    private readonly IPeerWorldLobby _lobby;
    private readonly UserIdentity _localUser;
    private readonly IPeerWorldRevisionExchange _revisionExchange;
    private readonly IPeerWorldBootstrapExchange _bootstrapExchange;

    public GenerationBoundPeerWorldExchange(
        IPeerWorldLobby lobby,
        UserIdentity localUser,
        IPeerWorldRevisionExchange revisionExchange,
        IPeerWorldBootstrapExchange bootstrapExchange)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(localUser);
        ArgumentNullException.ThrowIfNull(revisionExchange);
        ArgumentNullException.ThrowIfNull(bootstrapExchange);
        _lobby = lobby;
        _localUser = localUser;
        _revisionExchange = revisionExchange;
        _bootstrapExchange = bootstrapExchange;
    }

    public async Task<PeerWorldRevisionReceipt> TransferAsync(
        UserIdentity targetHost,
        PeerWorldRevisionOffer offer,
        Stream statePayload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetHost);
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(statePayload);
        var authority = offer.World.PeerAuthority
            ?? throw new InvalidDataException(
                "Peer handoff offer has no prospective persistent authority.");
        if (!SameUser(authority.Holder, targetHost))
        {
            throw new InvalidDataException(
                "Peer handoff offer authority holder does not match the requested target host.");
        }

        var live = await RequireConfirmedLocalLobbyAsync(
            offer.World.Id,
            cancellationToken);
        if (live.RequestedHost is null ||
            !SameUser(live.RequestedHost, targetHost))
        {
            throw new InvalidOperationException(
                "Peer handoff transfer target does not match the active lobby handoff request.");
        }

        ulong expectedNext;
        try
        {
            expectedNext = checked(live.AuthorityGeneration + 1);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "Live lobby authority generation cannot advance beyond UInt64.MaxValue.",
                exception);
        }

        if (authority.Generation != expectedNext)
        {
            throw new InvalidOperationException(
                $"Peer handoff offer generation {authority.Generation} does not follow live lobby generation {live.AuthorityGeneration}.");
        }

        return await _revisionExchange.TransferAsync(
            targetHost,
            offer,
            statePayload,
            cancellationToken);
    }

    public async Task<PeerWorldRevisionReceipt> TransferBootstrapAsync(
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
                "Peer bootstrap offer has no persistent authority.");
        if (!SameUser(authority.Holder, _localUser))
        {
            throw new InvalidOperationException(
                "Only the persistent local holder may bootstrap another participant.");
        }

        var live = await RequireConfirmedLocalLobbyAsync(
            offer.World.Id,
            cancellationToken);
        if (live.RequestedHost is not null)
        {
            throw new InvalidOperationException(
                "Peer bootstrap is not allowed while the live World is changing hosts.");
        }

        if (authority.Generation != live.AuthorityGeneration)
        {
            throw new InvalidOperationException(
                $"Peer bootstrap offer generation {authority.Generation} does not match live lobby generation {live.AuthorityGeneration}.");
        }

        return await _bootstrapExchange.TransferBootstrapAsync(
            targetMember,
            offer,
            statePayload,
            cancellationToken);
    }

    private async Task<PeerWorldLobbySnapshot> RequireConfirmedLocalLobbyAsync(
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        var live = await _lobby.GetAsync(worldId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"World '{worldId}' has no active peer lobby for transfer.");
        if (!live.OwnerConfirmed ||
            live.AuthorityGeneration == 0 ||
            !SameUser(live.Owner, _localUser))
        {
            throw new InvalidOperationException(
                $"World '{worldId}' live lobby does not confirm the local persistent authority holder.");
        }

        return live;
    }

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}
