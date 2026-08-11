using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamPeerLobbyParticipantCompositionTests
{
    [Fact]
    public void ParticipantEnumerationIsReadOnlyAndReusesExistingLobbyAuthorityValidation()
    {
        var source = Read("src/SharedWorlds.Desktop/SteamPeerWorldLobby.Participants.cs");

        Assert.Contains("ListCurrentMembersAsync(", source, StringComparison.Ordinal);
        Assert.Contains("EnsureLocalUser(expectedOwner);", source, StringComparison.Ordinal);
        Assert.Contains("EnsurePositiveGeneration(expectedAuthorityGeneration, worldId);", source, StringComparison.Ordinal);
        Assert.Contains("var lobbyId = RequireKnownLobby(worldId);", source, StringComparison.Ordinal);
        Assert.Contains("var current = ReadSnapshot(worldId, lobbyId);", source, StringComparison.Ordinal);
        Assert.Contains("EnsureWritableOwner(", source, StringComparison.Ordinal);
        Assert.Contains("SteamMatchmaking.GetNumLobbyMembers(lobbyId)", source, StringComparison.Ordinal);
        Assert.Contains("SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, index)", source, StringComparison.Ordinal);
        Assert.Contains("members.Add(ToUser(steamId));", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetLobbyData", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetLobbyOwner", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LeaveLobby", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestHandoffAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthorityHandoffStillPerformsItsOwnLiveMembershipRecheck()
    {
        var source = Read("src/SharedWorlds.Desktop/SteamPeerWorldLobby.cs");
        var request = RequiredIndex(source, "public async Task<PeerWorldLobbySnapshot> RequestHandoffAsync(");
        var parse = RequiredIndex(source, "var requestedSteamId = ParseSteamIdentity(requestedHost);", request);
        var member = RequiredIndex(source, "EnsureLobbyMember(lobbyId, requestedSteamId, worldId);", parse);
        var metadata = RequiredIndex(source, "RequestedHostKey", member);

        Assert.True(request < parse);
        Assert.True(parse < member);
        Assert.True(member < metadata);
    }

    private static string Read(string relativePath)
        => File.ReadAllText(FindRepositoryFile(relativePath));

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Required source fragment was not found: {value}");
        return index;
    }

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
