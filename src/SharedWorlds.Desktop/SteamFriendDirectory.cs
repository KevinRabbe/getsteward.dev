using System.Globalization;
using SharedWorlds.Core.Domain;
using Steamworks;

namespace SharedWorlds.Desktop;

/// <summary>
/// Small read-only adapter over Steam's immediate-friends list for Steward peer access management.
/// Steam remains the identity/delivery surface; canonical World membership remains stored on the World.
/// </summary>
internal sealed class SteamFriendDirectory
{
    private const EFriendFlags FriendFlags = EFriendFlags.k_EFriendFlagImmediate;

    private readonly SteamPlatformRuntime _platform;

    public SteamFriendDirectory(SteamPlatformRuntime platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        _platform = platform;
    }

    public async Task<IReadOnlyList<UserIdentity>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_platform.Dispatcher.CheckAccess())
        {
            return ReadFriends();
        }

        return await _platform.Dispatcher.InvokeAsync(
            ReadFriends,
            System.Windows.Threading.DispatcherPriority.Normal,
            cancellationToken).Task;
    }

    private IReadOnlyList<UserIdentity> ReadFriends()
    {
        var count = SteamFriends.GetFriendCount(FriendFlags);
        if (count < 0)
        {
            throw new InvalidOperationException(
                "Steam could not enumerate the current friends list.");
        }

        var friends = new Dictionary<ulong, UserIdentity>();
        for (var index = 0; index < count; index++)
        {
            var steamId = SteamFriends.GetFriendByIndex(index, FriendFlags);
            if (steamId == CSteamID.Nil ||
                steamId.m_SteamID == 0 ||
                steamId == _platform.LocalSteamId)
            {
                continue;
            }

            var externalId = steamId.m_SteamID.ToString(CultureInfo.InvariantCulture);
            var displayName = SteamFriends.GetFriendPersonaName(steamId);
            friends[steamId.m_SteamID] = new UserIdentity(
                "steam",
                externalId,
                string.IsNullOrWhiteSpace(displayName)
                    ? $"Steam user {externalId}"
                    : displayName.Trim());
        }

        return friends.Values
            .OrderBy(friend => friend.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(friend => friend.ExternalId, StringComparer.Ordinal)
            .ToArray();
    }
}
