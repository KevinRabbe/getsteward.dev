using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.Infrastructure.Sessions;

/// <summary>
/// Host-local result of authorizing one authenticated peer for the managed game data plane. The real
/// game port is intentionally returned only to the host-side bridge implementation; it is not lobby
/// metadata and is never required by the joining game client. JoinToken may be sent through the
/// authenticated bridge Ready response because the game adapter still consumes its ordinary
/// HostConnection contract on the joining PC.
/// </summary>
public sealed record PeerGameDatagramBridgeGrant(
    WorldId WorldId,
    UserIdentity RemoteMember,
    UserIdentity Host,
    ulong AuthorityGeneration,
    int HostUdpPort,
    string? JoinToken);

/// <summary>
/// Proves that an already-authenticated peer may open a UDP game-data bridge to the currently running
/// managed host. Authentication of remoteUser is a transport responsibility; this service supplies the
/// authorization barrier that binds that identity to Steward's live and persistent World authority.
/// </summary>
public sealed class PeerGameDatagramBridgeAdmissionService
{
    private readonly IPeerWorldLobby _lobby;
    private readonly IWorldStorage _storage;
    private readonly IPeerManagedHostPresenceRegistry _presence;
    private readonly UserIdentity _localUser;

    public PeerGameDatagramBridgeAdmissionService(
        IPeerWorldLobby lobby,
        IWorldStorage storage,
        IPeerManagedHostPresenceRegistry presence,
        UserIdentity localUser)
    {
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(presence);
        ArgumentNullException.ThrowIfNull(localUser);
        _lobby = lobby;
        _storage = storage;
        _presence = presence;
        _localUser = localUser;
    }

    public async Task<PeerGameDatagramBridgeGrant> AuthorizeAsync(
        WorldId worldId,
        ulong expectedAuthorityGeneration,
        UserIdentity remoteUser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteUser);
        if (expectedAuthorityGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedAuthorityGeneration),
                "Peer game bridge admission requires a nonzero authority generation.");
        }

        if (SameUser(remoteUser, _localUser))
        {
            throw new WorldSessionConflictException(
                worldId,
                "The local host cannot open a peer game bridge to itself.");
        }

        var lobby = await _lobby.GetAsync(worldId, cancellationToken)
            ?? throw new WorldSessionConflictException(
                worldId,
                "There is no active peer lobby for this World.");
        if (lobby.WorldId != worldId ||
            !lobby.OwnerConfirmed ||
            lobby.AuthorityGeneration != expectedAuthorityGeneration ||
            !SameUser(lobby.Owner, _localUser))
        {
            throw new WorldSessionConflictException(
                worldId,
                "The local Steward instance is not the confirmed live host at the requested authority generation.");
        }

        if (lobby.RequestedHost is not null)
        {
            throw new WorldSessionConflictException(
                worldId,
                "New game bridge admission is disabled while the World is changing hosts.");
        }

        var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new WorldSessionConflictException(
                worldId,
                "The local canonical World metadata is missing.");
        if (world.SharingMode != WorldSharingMode.Shared ||
            world.PeerAuthority is not { } authority ||
            authority.Generation != expectedAuthorityGeneration ||
            !SameUser(authority.Holder, _localUser))
        {
            throw new WorldSessionConflictException(
                worldId,
                "Persistent World authority does not match the confirmed live peer host.");
        }

        if (!ContainsStableMember(world.Members, _localUser) ||
            !ContainsStableMember(world.Members, remoteUser))
        {
            throw new WorldSessionConflictException(
                worldId,
                "Peer game bridge admission requires both host and remote participant to be canonical World members.");
        }

        var presence = await _presence.GetAsync(worldId, cancellationToken)
            ?? throw new WorldSessionConflictException(
                worldId,
                "The managed game host has not published local joinability yet.");
        if (presence.State != PeerManagedHostPresenceState.Ready ||
            presence.Endpoint is null ||
            presence.AuthorityGeneration != expectedAuthorityGeneration ||
            !SameUser(presence.Holder, _localUser))
        {
            throw new WorldSessionConflictException(
                worldId,
                "Managed host presence does not match the confirmed live peer authority.");
        }

        var hostUdpPort = presence.Endpoint.Port
            ?? throw new NotSupportedException(
                $"World '{worldId}' managed game adapter does not expose a UDP-compatible join port.");
        if (hostUdpPort is < 1 or > 65535)
        {
            throw new InvalidDataException(
                $"World '{worldId}' managed game endpoint contains invalid UDP port '{hostUdpPort}'.");
        }

        return new PeerGameDatagramBridgeGrant(
            worldId,
            remoteUser,
            _localUser,
            expectedAuthorityGeneration,
            hostUdpPort,
            presence.Endpoint.JoinToken);
    }

    /// <summary>
    /// Converts an authenticated peer bridge's local loopback UDP listener into the existing adapter
    /// join contract. The host's real network address/port remain hidden behind the peer data plane.
    /// </summary>
    public static HostConnection CreateLoopbackClientConnection(
        int localUdpPort,
        string? joinToken)
    {
        if (localUdpPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localUdpPort),
                "Local peer game bridge UDP port must be between 1 and 65535.");
        }

        return new HostConnection(
            "127.0.0.1",
            localUdpPort,
            joinToken);
    }

    private static bool ContainsStableMember(
        IReadOnlyList<UserIdentity> members,
        UserIdentity expected)
        => members.Any(member => SameUser(member, expected));

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}
