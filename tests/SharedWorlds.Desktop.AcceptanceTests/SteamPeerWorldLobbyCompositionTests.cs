using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamPeerWorldLobbyCompositionTests
{
    [Fact]
    public void LobbyUsesSteamOwnerButRequiresSeparateStewardAuthorityConfirmation()
    {
        var source = ReadLobby();

        Assert.Contains(
            "var owner = SteamMatchmaking.GetLobbyOwner(lobbyId);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const string AuthorityOwnerKey = \"steward.authority-owner\";",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "OwnerConfirmed: observedOwner == authorityOwner",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "all surviving clients observe RecoveryPending",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GracefulTransferPublishesCommittedRevisionBeforeChangingSteamOwner()
    {
        var source = ReadLobby();
        var method = RequiredIndex(source, "private PeerWorldLobbySnapshot TransferOwnershipCore(");
        var committedRevision = RequiredIndex(
            source,
            "CommittedRevisionKey,\n            committedRevision.ToString(),",
            method);
        var authorityOwner = RequiredIndex(
            source,
            "AuthorityOwnerKey,\n            SteamIdText(newOwnerSteamId),",
            committedRevision);
        var ownerTransfer = RequiredIndex(
            source,
            "SteamMatchmaking.SetLobbyOwner(lobbyId, newOwnerSteamId)",
            authorityOwner);
        var verifyOwner = RequiredIndex(
            source,
            "var observedOwner = SteamMatchmaking.GetLobbyOwner(lobbyId);",
            ownerTransfer);

        Assert.True(committedRevision < authorityOwner);
        Assert.True(authorityOwner < ownerTransfer);
        Assert.True(ownerTransfer < verifyOwner);
    }

    [Fact]
    public void NormalHostExitCannotLetSteamChooseReplacementBehindSteward()
    {
        var source = ReadLobby();
        var leave = RequiredIndex(source, "public async Task LeaveAsync(");
        var remainingMembers = RequiredIndex(
            source,
            "SteamMatchmaking.GetNumLobbyMembers(lobbyId) > 1",
            leave);
        var conflict = RequiredIndex(
            source,
            "Steward must complete host handoff before the current host leaves.",
            remainingMembers);
        var steamLeave = RequiredIndex(
            source,
            "SteamMatchmaking.LeaveLobby(lobbyId);",
            conflict);

        Assert.True(remainingMembers < conflict);
        Assert.True(conflict < steamLeave);
    }

    [Fact]
    public void LobbyStoresCoordinationMetadataOnlyAndDoesNotOwnSteamLifetime()
    {
        var source = ReadLobby();

        Assert.DoesNotContain("SteamAPI.Init", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamAPI.Shutdown", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StatePackage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StoreRevision", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenRevision", source, StringComparison.Ordinal);
        Assert.Contains("Durable World bytes never live in lobby metadata.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SteamIdentityComparisonIgnoresPersonaDisplayName()
    {
        var source = ReadLobby();

        Assert.Contains(
            "private static bool SameUser(UserIdentity left, UserIdentity right)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("left.Provider", source, StringComparison.Ordinal);
        Assert.Contains("left.ExternalId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("left.DisplayName", source, StringComparison.Ordinal);
        Assert.DoesNotContain("right.DisplayName", source, StringComparison.Ordinal);
        Assert.DoesNotContain("current.RequestedHost != newOwner", source, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.Owner != expectedOwner", source, StringComparison.Ordinal);
        Assert.DoesNotContain("user != _platform.LocalUser", source, StringComparison.Ordinal);
    }

    private static string ReadLobby()
        => File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/SteamPeerWorldLobby.cs"));

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
