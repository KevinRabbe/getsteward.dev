using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;
using Steamworks;

namespace SharedWorlds.Desktop;

internal sealed partial class SteamPeerWorldLobby
{
    /// <summary>
    /// Detaches this non-holder participant from a Steam lobby after the current canonical authority
    /// holder has already acknowledged membership removal. Lobby presence is never membership authority.
    /// The current observed Steam lobby owner must use Steward's explicit handoff/host lifecycle instead.
    /// </summary>
    public async Task LeaveJoinedLobbyAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            await InvokeSteamAsync(
                () =>
                {
                    if (!_knownLobbies.TryGetValue(worldId, out var lobbyId))
                    {
                        return;
                    }

                    var current = ReadSnapshot(worldId, lobbyId);
                    if (SameUser(current.Owner, _platform.LocalUser))
                    {
                        throw new WorldSessionConflictException(
                            worldId,
                            "The current Steam lobby owner cannot use participant Leave World cleanup. Hand off host authority first.");
                    }

                    SteamMatchmaking.LeaveLobby(lobbyId);
                    _knownLobbies.Remove(worldId);
                },
                cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }
}
