using SharedWorlds.Core.Domain;
using Steamworks;

namespace SharedWorlds.Desktop;

internal sealed partial class SteamPeerWorldLobby
{
    /// <summary>
    /// Returns the Steam users currently present in this installation's known lobby for the exact
    /// confirmed authority owner/generation. This is presentation evidence only; handoff still
    /// revalidates lobby membership inside RequestHandoffAsync before any authority mutation occurs.
    /// </summary>
    internal async Task<IReadOnlyList<UserIdentity>> ListCurrentMembersAsync(
        WorldId worldId,
        UserIdentity expectedOwner,
        ulong expectedAuthorityGeneration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        EnsureLocalUser(expectedOwner);
        EnsurePositiveGeneration(expectedAuthorityGeneration, worldId);

        return await InvokeSteamAsync(
            () =>
            {
                var lobbyId = RequireKnownLobby(worldId);
                var current = ReadSnapshot(worldId, lobbyId);
                EnsureWritableOwner(
                    current,
                    expectedOwner,
                    expectedAuthorityGeneration);

                var count = SteamMatchmaking.GetNumLobbyMembers(lobbyId);
                var seen = new HashSet<ulong>();
                var members = new List<UserIdentity>(Math.Max(0, count));
                for (var index = 0; index < count; index++)
                {
                    var steamId = SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, index);
                    if (steamId.m_SteamID == 0 || !seen.Add(steamId.m_SteamID))
                    {
                        continue;
                    }

                    members.Add(ToUser(steamId));
                }

                return (IReadOnlyList<UserIdentity>)members
                    .OrderBy(member => member.ExternalId, StringComparer.Ordinal)
                    .ToArray();
            },
            cancellationToken);
    }
}
