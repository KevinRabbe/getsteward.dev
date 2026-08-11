using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamFriendAccessPickerCompositionTests
{
    [Fact]
    public void FriendDirectoryUsesImmediateSteamFriendsOnProcessSteamDispatcher()
    {
        var source = Read("src/SharedWorlds.Desktop/SteamFriendDirectory.cs");

        Assert.Contains(
            "private const EFriendFlags FriendFlags = EFriendFlags.k_EFriendFlagImmediate;",
            source,
            StringComparison.Ordinal);
        Assert.Contains("SteamFriends.GetFriendCount(FriendFlags)", source, StringComparison.Ordinal);
        Assert.Contains("SteamFriends.GetFriendByIndex(index, FriendFlags)", source, StringComparison.Ordinal);
        Assert.Contains("SteamFriends.GetFriendPersonaName(steamId)", source, StringComparison.Ordinal);
        Assert.Contains("_platform.Dispatcher.CheckAccess()", source, StringComparison.Ordinal);
        Assert.Contains("_platform.Dispatcher.InvokeAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendDirectoryNeverReturnsLocalOrInvalidSteamIdentity()
    {
        var source = Read("src/SharedWorlds.Desktop/SteamFriendDirectory.cs");
        var loop = RequiredIndex(source, "for (var index = 0; index < count; index++)");
        var nil = RequiredIndex(source, "steamId == CSteamID.Nil", loop);
        var zero = RequiredIndex(source, "steamId.m_SteamID == 0", nil);
        var local = RequiredIndex(source, "steamId == _platform.LocalSteamId", zero);
        var identity = RequiredIndex(source, "new UserIdentity(", local);

        Assert.True(loop < nil);
        Assert.True(nil < zero);
        Assert.True(zero < local);
        Assert.True(local < identity);
        Assert.Contains("\"steam\",", source[identity..], StringComparison.Ordinal);
    }

    [Fact]
    public void PeerRuntimeExposesOneSteamFriendDirectoryFromExistingSteamLifetime()
    {
        var source = Read("src/SharedWorlds.Desktop/StewardDesktopPeerRuntime.cs");

        Assert.Equal(1, CountOccurrences(source, "new SteamFriendDirectory(platform)"));
        Assert.Contains("public SteamFriendDirectory Friends { get; }", source, StringComparison.Ordinal);
        Assert.Contains("Friends = new SteamFriendDirectory(platform);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamAPI.Init", Read("src/SharedWorlds.Desktop/SteamFriendDirectory.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("SteamAPI.Shutdown", Read("src/SharedWorlds.Desktop/SteamFriendDirectory.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void AccessDialogUsesFriendPickerAndRemovesManualId64Entry()
    {
        var source = Read("src/SharedWorlds.Desktop/PeerWorldAccessDialog.cs");

        Assert.Contains("private readonly ComboBox _friendPicker", source, StringComparison.Ordinal);
        Assert.Contains("_runtime.Friends.ListAsync()", source, StringComparison.Ordinal);
        Assert.Contains(
            ".Where(friend => !_world.Members.Any(member => SameUser(member, friend)))",
            source,
            StringComparison.Ordinal);
        Assert.Contains("TryGetSelectedAvailableFriend(out var member)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_steamId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ulong.TryParse", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Steam ID64 to add", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Enter the person's numeric Steam ID64", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendSelectionStillCommitsCanonicalMembershipBeforeInviteDelivery()
    {
        var source = Read("src/SharedWorlds.Desktop/PeerWorldAccessDialog.cs");
        var add = RequiredIndex(source, "private async void AddButton_Click");
        var selected = RequiredIndex(source, "TryGetSelectedAvailableFriend(out var member)", add);
        var membership = RequiredIndex(source, "_runtime.Membership.AddMemberAsync(", selected);
        var invitation = RequiredIndex(source, "_runtime.Invitations.InviteCanonicalMembersAsync(", membership);

        Assert.True(add < selected);
        Assert.True(selected < membership);
        Assert.True(membership < invitation);
    }

    [Fact]
    public void FriendPickerMutabilityRemainsHolderOnlyAndRemoveLeaveRemainPresent()
    {
        var source = Read("src/SharedWorlds.Desktop/PeerWorldAccessDialog.cs");
        var holder = RequiredIndex(source, "_canManage = authority is not null &&");
        var holderIdentity = RequiredIndex(source, "SameUser(authority.Holder, _runtime.User)", holder);
        var friends = RequiredIndex(source, "var friends = await _runtime.Friends.ListAsync();", holderIdentity);
        var update = RequiredIndex(source, "private void UpdateActionState()", friends);
        var canMutate = RequiredIndex(source, "var canMutate = !_busy && _canManage;", update);
        var friendEnable = RequiredIndex(source, "_friendPicker.IsEnabled = canMutate", canMutate);
        var addEnable = RequiredIndex(source, "_addButton.IsEnabled = canMutate", friendEnable);

        Assert.True(holder < holderIdentity);
        Assert.True(holderIdentity < friends);
        Assert.True(friends < update);
        Assert.True(update < canMutate);
        Assert.True(canMutate < friendEnable);
        Assert.True(friendEnable < addEnable);
        Assert.Contains("_runtime.MemberRemoval.RemoveMemberAsync", source, StringComparison.Ordinal);
        Assert.Contains("new PeerWorldLeaveService(", source, StringComparison.Ordinal);
        Assert.Contains("Content = \"Leave World\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FriendEnumerationFailureDoesNotChangeCanonicalAccessState()
    {
        var source = Read("src/SharedWorlds.Desktop/PeerWorldAccessDialog.cs");
        var reload = RequiredIndex(source, "private async Task ReloadAsync");
        var friendTry = RequiredIndex(source, "var friends = await _runtime.Friends.ListAsync();", reload);
        var friendCatch = RequiredIndex(source, "catch (Exception exception)", friendTry);
        var problem = RequiredIndex(source, "friendLoadProblem = exception.Message;", friendCatch);
        var update = RequiredIndex(source, "UpdateActionState();", problem);
        var block = source[friendTry..update];

        Assert.DoesNotContain("Membership.AddMemberAsync", block, StringComparison.Ordinal);
        Assert.DoesNotContain("MemberRemoval.RemoveMemberAsync", block, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveWorldAsync", block, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while (true)
        {
            var index = source.IndexOf(value, offset, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            offset = index + value.Length;
        }
    }

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Required source fragment was not found: {value}");
        return index;
    }

    private static string Read(string relativePath)
        => File.ReadAllText(FindRepositoryFile(relativePath));

    private static string FindRepositoryFile(string relativePath)
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var candidate = Path.Combine(workspace, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }
}
