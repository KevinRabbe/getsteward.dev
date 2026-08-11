using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Sessions;

namespace SharedWorlds.Desktop;

internal sealed record PeerWorldLeaveCompletion(
    PeerWorldLeaveResult CanonicalResult,
    bool SteamLobbyDetached,
    string? CleanupWarning);

/// <summary>
/// Requester-side Leave World lifecycle. The local replica is never authoritative evidence of leaving:
/// Steward first asks the confirmed current holder to remove this authenticated member canonically.
/// Only after that exact-generation acknowledgement does it delete the local replica. Steam lobby exit
/// is final best-effort platform cleanup and is intentionally not treated as membership authority.
/// </summary>
internal sealed class PeerWorldLeaveService
{
    private readonly IWorldStorage _storage;
    private readonly SteamPeerWorldLobby _lobby;
    private readonly IPeerWorldLeaveRequestClient _requests;
    private readonly UserIdentity _localUser;

    public PeerWorldLeaveService(
        IWorldStorage storage,
        SteamPeerWorldLobby lobby,
        IPeerWorldLeaveRequestClient requests,
        UserIdentity localUser)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(lobby);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(localUser);
        _storage = storage;
        _lobby = lobby;
        _requests = requests;
        _localUser = localUser;
    }

    public async Task<PeerWorldLeaveCompletion> LeaveAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        var world = await _storage.LoadWorldAsync(worldId, cancellationToken)
            ?? throw new InvalidOperationException(
                "The local World replica is no longer available.");
        if (world.SharingMode != WorldSharingMode.Shared ||
            world.PeerAuthority is not { Generation: > 0 } authority ||
            !ContainsStableMember(world.Members, _localUser))
        {
            throw new InvalidOperationException(
                "Leave World requires a current shared peer replica whose canonical membership includes this Steward account.");
        }

        if (SameUser(authority.Holder, _localUser))
        {
            throw new InvalidOperationException(
                "You are the current World host authority. Hand off host first, then use Leave World after the new holder is active.");
        }

        var liveLobby = await _lobby.GetAsync(worldId, cancellationToken)
            ?? throw new InvalidOperationException(
                "Leave World requires the current authority holder to be actively hosting this World.");
        if (!liveLobby.OwnerConfirmed ||
            liveLobby.AuthorityGeneration != authority.Generation ||
            !SameUser(liveLobby.Owner, authority.Holder) ||
            liveLobby.RequestedHost is not null)
        {
            throw new InvalidOperationException(
                "Leave World is available only against the confirmed current host/generation and not during host handoff.");
        }

        var canonicalResult = await _requests.RequestLeaveAsync(
            authority.Holder,
            new PeerWorldLeaveRequest(worldId, authority.Generation),
            cancellationToken);
        if (canonicalResult.WorldId != worldId ||
            canonicalResult.AuthorityGeneration != authority.Generation ||
            canonicalResult.CurrentStateRevisionId.Value == Guid.Empty)
        {
            throw new InvalidDataException(
                "Leave World acknowledgement does not match the requested World and authority generation.");
        }

        // From this point the current authority holder has already removed this account canonically.
        // Caller cancellation must no longer strand a stale local replica that looks usable. Finish the
        // irreversible local side with an independent token: delete/verify first, then best-effort Steam
        // lobby cleanup. Process termination is the only remaining interruption boundary.
        var cleanupToken = CancellationToken.None;
        await _storage.DeleteWorldAsync(worldId, cleanupToken);
        if (await _storage.LoadWorldAsync(worldId, cleanupToken) is not null)
        {
            throw new IOException(
                "Canonical Leave World succeeded, but Steward could not delete the local World replica.");
        }

        try
        {
            await _lobby.LeaveJoinedLobbyAsync(worldId, cleanupToken);
            return new PeerWorldLeaveCompletion(
                canonicalResult,
                SteamLobbyDetached: true,
                CleanupWarning: null);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException)
        {
            // Lobby presence is not access. Canonical removal and local replica deletion already
            // succeeded, so do not report the World as retained merely because Steam cleanup failed.
            return new PeerWorldLeaveCompletion(
                canonicalResult,
                SteamLobbyDetached: false,
                CleanupWarning:
                    $"World access and the local replica were removed, but Steam lobby cleanup did not finish: {exception.Message}");
        }
    }

    private static bool ContainsStableMember(
        IReadOnlyList<UserIdentity> members,
        UserIdentity expected)
        => members.Any(member => SameUser(member, expected));

    private static bool SameUser(UserIdentity left, UserIdentity right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(left.ExternalId, right.ExternalId, StringComparison.Ordinal);
}
