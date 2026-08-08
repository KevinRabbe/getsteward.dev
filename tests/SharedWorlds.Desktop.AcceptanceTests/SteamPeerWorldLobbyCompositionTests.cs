using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamPeerWorldLobbyCompositionTests
{
    [Fact]
    public void LobbyUsesSchemaTwoOwnerAndGenerationAuthorityMetadata()
    {
        var source = ReadLobby();

        Assert.Contains(
            "private const string SchemaVersion = \"2\";",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const string AuthorityOwnerKey = \"steward.authority-owner\";",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const string AuthorityGenerationKey = \"steward.authority-generation\";",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "var owner = SteamMatchmaking.GetLobbyOwner(lobbyId);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "OwnerConfirmed: observedOwner == authorityOwner",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AuthorityGeneration: authorityGeneration",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GracefulTransferInvalidatesOldOwnerBeforePublishingGenerationRevisionAndSteamOwnerMove()
    {
        var source = ReadLobby();
        var method = RequiredIndex(source, "private PeerWorldLobbySnapshot TransferOwnershipCore(");
        var authorityOwner = RequiredIndex(
            source,
            "AuthorityOwnerKey,\n                SteamIdText(newOwnerSteamId),",
            method);
        var authorityGeneration = RequiredIndex(
            source,
            "AuthorityGenerationKey,\n                GenerationText(newAuthorityGeneration),",
            authorityOwner);
        var committedRevision = RequiredIndex(
            source,
            "CommittedRevisionKey,\n                committedRevision.ToString(),",
            authorityGeneration);
        var ownerTransfer = RequiredIndex(
            source,
            "SteamMatchmaking.SetLobbyOwner(",
            committedRevision);
        var verifyOwner = RequiredIndex(
            source,
            "var observedOwner = SteamMatchmaking.GetLobbyOwner(lobbyId);",
            ownerTransfer);

        Assert.True(authorityOwner < authorityGeneration);
        Assert.True(authorityGeneration < committedRevision);
        Assert.True(committedRevision < ownerTransfer);
        Assert.True(ownerTransfer < verifyOwner);
    }

    [Fact]
    public void AnyCommittedHandoffPublicationFailureMakesSourceAbandonLiveLobby()
    {
        var source = ReadLobby();
        var method = RequiredIndex(source, "private PeerWorldLobbySnapshot TransferOwnershipCore(");
        var mutationTry = RequiredIndex(source, "        try\n        {", method);
        var abandonCall = RequiredIndex(
            source,
            "AbandonCommittedHandoffLobby(lobbyId, worldId);",
            mutationTry);
        var abandonMethod = RequiredIndex(
            source,
            "private void AbandonCommittedHandoffLobby(",
            abandonCall);
        var stopJoin = RequiredIndex(
            source,
            "SteamMatchmaking.SetLobbyJoinable(lobbyId, false)",
            abandonMethod);
        var steamLeave = RequiredIndex(
            source,
            "SteamMatchmaking.LeaveLobby(lobbyId);",
            stopJoin);
        var detach = RequiredIndex(
            source,
            "_knownLobbies.Remove(worldId);",
            steamLeave);

        Assert.DoesNotContain("RestoreLobbyData", source, StringComparison.Ordinal);
        Assert.DoesNotContain("previousAuthority", source, StringComparison.Ordinal);
        Assert.True(mutationTry < abandonCall);
        Assert.True(abandonCall < abandonMethod);
        Assert.True(abandonMethod < stopJoin);
        Assert.True(stopJoin < steamLeave);
        Assert.True(steamLeave < detach);
    }

    [Fact]
    public void EveryAuthorityMutationAcceptsExplicitGenerationFence()
    {
        var source = ReadLobby();

        Assert.Contains(
            "UserIdentity proposedOwner,\n        ulong authorityGeneration,",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "UserIdentity expectedOwner,\n        ulong expectedAuthorityGeneration,\n        UserIdentity requestedHost,",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ulong expectedAuthorityGeneration,\n        UserIdentity newOwner,\n        ulong newAuthorityGeneration,",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "UserIdentity expectedOwner,\n        ulong expectedAuthorityGeneration,\n        CancellationToken cancellationToken",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "newGeneration != expectedGeneration + 1",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void HandoffMutationGateCannotOverwriteTargetOrLeavePendingTransfer()
    {
        var source = ReadLobby();
        var request = RequiredIndex(source, "public async Task<PeerWorldLobbySnapshot> RequestHandoffAsync(");
        var conflictingRequest = RequiredIndex(
            source,
            "current.RequestedHost is not null &&\n                        !SameUser(current.RequestedHost, requestedHost)",
            request);
        var requestWrite = RequiredIndex(
            source,
            "RequestedHostKey,\n                        SteamIdText(requestedSteamId),",
            conflictingRequest);
        Assert.True(conflictingRequest < requestWrite);

        var leave = RequiredIndex(source, "public async Task LeaveAsync(");
        var pendingGuard = RequiredIndex(
            source,
            "if (current.RequestedHost is not null)",
            leave);
        var steamLeave = RequiredIndex(
            source,
            "SteamMatchmaking.LeaveLobby(lobbyId);",
            pendingGuard);
        Assert.True(pendingGuard < steamLeave);
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
        Assert.Contains("Durable World bytes never live in lobby data.", source, StringComparison.Ordinal);
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
