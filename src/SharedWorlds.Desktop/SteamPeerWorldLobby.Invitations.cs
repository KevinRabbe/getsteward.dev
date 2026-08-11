using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Errors;
using SharedWorlds.Infrastructure.Sessions;
using Steamworks;

namespace SharedWorlds.Desktop;

/// <summary>
/// Steam invitation delivery for a member that has already been authorized in canonical World metadata.
/// This partial owns only provider transport checks and the process-local lobby handle; it never decides
/// who belongs to the World and never persists membership from Steam lobby presence.
/// </summary>
internal sealed partial class SteamPeerWorldLobby : IPeerWorldMemberInvitationTransport
{
    public bool Supports(UserIdentity member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return string.Equals(member.Provider, "steam", StringComparison.OrdinalIgnoreCase) &&
               TryParseSteamId(member.ExternalId, out var steamId) &&
               steamId != _platform.LocalSteamId;
    }

    public async Task InviteAsync(
        WorldId worldId,
        UserIdentity expectedHolder,
        ulong authorityGeneration,
        UserIdentity member,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedHolder);
        ArgumentNullException.ThrowIfNull(member);
        EnsureLocalUser(expectedHolder);
        EnsurePositiveGeneration(authorityGeneration, worldId);
        var memberSteamId = ParseSteamIdentity(member);
        if (memberSteamId == _platform.LocalSteamId)
        {
            throw new InvalidOperationException(
                "Steward cannot invite the local Steam user to its own World lobby.");
        }

        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            await InvokeSteamAsync(
                () =>
                {
                    var lobbyId = RequireKnownLobby(worldId);
                    var current = ReadSnapshot(worldId, lobbyId);
                    EnsureWritableOwner(
                        current,
                        expectedHolder,
                        authorityGeneration);
                    if (current.RequestedHost is not null)
                    {
                        throw new WorldSessionConflictException(
                            worldId,
                            "Steward does not send new lobby invitations while host handoff is in progress.");
                    }

                    var count = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
                    for (var index = 0; index < count; index++)
                    {
                        if (SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, index) == memberSteamId)
                        {
                            // Delivery is idempotent. The canonical member is already attached to the
                            // exact live private lobby, so no second Steam invitation is required.
                            return;
                        }
                    }

                    if (!SteamMatchmaking.InviteUserToLobby(lobbyId, memberSteamId))
                    {
                        throw new IOException(
                            $"Steam could not deliver the Steward World invitation to member '{member.ExternalId}'.");
                    }
                },
                cancellationToken);
        }
        finally
        {
            _mutationGate.Release();
        }
    }
}
