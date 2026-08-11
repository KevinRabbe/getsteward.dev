using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamPeerWorldMemberInvitationCompositionTests
{
    [Fact]
    public void SteamLobbyInvitationIsAProviderTransportOnly()
    {
        var baseLobby = Read("src/SharedWorlds.Desktop/SteamPeerWorldLobby.cs");
        var invitations = Read("src/SharedWorlds.Desktop/SteamPeerWorldLobby.Invitations.cs");

        Assert.Contains(
            "internal sealed partial class SteamPeerWorldLobby : IPeerWorldLobby",
            baseLobby,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal sealed partial class SteamPeerWorldLobby : IPeerWorldMemberInvitationTransport",
            invitations,
            StringComparison.Ordinal);
        Assert.Contains("SteamMatchmaking.InviteUserToLobby(lobbyId, memberSteamId)", invitations, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveWorldAsync", invitations, StringComparison.Ordinal);
        Assert.DoesNotContain("Members =", invitations, StringComparison.Ordinal);
        Assert.DoesNotContain("PeerWorldMembershipService", invitations, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderRevalidatesExactLiveOwnerGenerationAndRefusesHandoff()
    {
        var source = Read("src/SharedWorlds.Desktop/SteamPeerWorldLobby.Invitations.cs");
        var method = RequiredIndex(source, "public async Task InviteAsync(");
        var localHolder = RequiredIndex(source, "EnsureLocalUser(expectedHolder);", method);
        var generation = RequiredIndex(source, "EnsurePositiveGeneration(authorityGeneration, worldId);", localHolder);
        var lobby = RequiredIndex(source, "var lobbyId = RequireKnownLobby(worldId);", generation);
        var snapshot = RequiredIndex(source, "var current = ReadSnapshot(worldId, lobbyId);", lobby);
        var authority = RequiredIndex(source, "EnsureWritableOwner(", snapshot);
        var handoff = RequiredIndex(source, "if (current.RequestedHost is not null)", authority);
        var invite = RequiredIndex(source, "SteamMatchmaking.InviteUserToLobby", handoff);

        Assert.True(method < localHolder);
        Assert.True(localHolder < generation);
        Assert.True(generation < lobby);
        Assert.True(lobby < snapshot);
        Assert.True(snapshot < authority);
        Assert.True(authority < handoff);
        Assert.True(handoff < invite);
    }

    [Fact]
    public void ProviderSupportsOnlyStableRemoteSteamIdentitiesAndIsIdempotentForJoinedMember()
    {
        var source = Read("src/SharedWorlds.Desktop/SteamPeerWorldLobby.Invitations.cs");

        Assert.Contains("string.Equals(member.Provider, \"steam\"", source, StringComparison.Ordinal);
        Assert.Contains("TryParseSteamId(member.ExternalId, out var steamId)", source, StringComparison.Ordinal);
        Assert.Contains("steamId != _platform.LocalSteamId", source, StringComparison.Ordinal);
        Assert.Contains("Steward cannot invite the local Steam user", source, StringComparison.Ordinal);
        Assert.Contains("SteamMatchmaking.GetNumLobbyMembers(lobbyId)", source, StringComparison.Ordinal);
        Assert.Contains("SteamMatchmaking.GetLobbyMemberByIndex(lobbyId, index) == memberSteamId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeComposesCanonicalInvitationSelectionAfterPresenceAndBeforeLifecycle()
    {
        var source = Read("src/SharedWorlds.Desktop/StewardDesktopPeerRuntime.cs");
        var presence = RequiredIndex(source, "var presenceCoordinator = new PeerManagedHostPresenceSessionCoordinator(");
        var service = RequiredIndex(source, "var invitations = new PeerWorldMemberInvitationService(", presence);
        var transport = RequiredIndex(source, "authorityFences,\n                lobby);", service);
        var decorator = RequiredIndex(source, "var sessionCoordinator = new PeerWorldMemberInvitationSessionCoordinator(", transport);
        var lifecycle = RequiredIndex(source, "var lifecycle = new WorldLifecycleService(", decorator);

        Assert.True(presence < service);
        Assert.True(service < transport);
        Assert.True(transport < decorator);
        Assert.True(decorator < lifecycle);
        Assert.Contains("public PeerWorldMemberInvitationService Invitations { get; }", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PrivateFriendsOnlyLobbyModelRemainsUnchanged()
    {
        var baseLobby = Read("src/SharedWorlds.Desktop/SteamPeerWorldLobby.cs");
        var invitations = Read("src/SharedWorlds.Desktop/SteamPeerWorldLobby.Invitations.cs");

        Assert.Contains("ELobbyType.k_ELobbyTypeFriendsOnly", baseLobby, StringComparison.Ordinal);
        Assert.DoesNotContain("k_ELobbyTypePublic", invitations, StringComparison.Ordinal);
        Assert.DoesNotContain("k_ELobbyTypeInvisible", invitations, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestLobbyList", invitations, StringComparison.Ordinal);
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
