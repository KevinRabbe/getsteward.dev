using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PeerWorldJoinCutoverCompositionTests
{
    [Fact]
    public void ExistingJoinButtonRoutesLocalPeerWorldsBeforeLegacyRemotePresence()
    {
        var source = ReadJoin();
        var handler = RequiredIndex(source, "private async void WorldJoinButton_Click(");
        var peerBranch = RequiredIndex(source, "if (_peerWorldIds.Contains(world.Id))", handler);
        var peerJoin = RequiredIndex(source, "await JoinPeerWorldAsync(world, peerAdapter);", peerBranch);
        var remoteRuntime = RequiredIndex(source, "var runtime = _remoteRuntime;", peerJoin);

        Assert.True(handler < peerBranch);
        Assert.True(peerBranch < peerJoin);
        Assert.True(peerJoin < remoteRuntime);
    }

    [Fact]
    public void ExplicitSteamInviteAttachesLobbyThenRequestsBootstrapOrCatchUp()
    {
        var source = ReadJoin();
        var subscription = RequiredIndex(
            source,
            "peerRuntime.LobbyJoin.JoinRequested += PeerWorldLobbyJoinRequested;");
        var handler = RequiredIndex(source, "private async Task HandlePeerWorldLobbyJoinRequestedAsync(", subscription);
        var joinLobby = RequiredIndex(source, "var joined = await runtime.LobbyJoin.JoinAsync(lobbyId);", handler);
        var liveLobby = RequiredIndex(source, "var snapshot = await RequirePeerJoinLobbyAsync(", joinLobby);
        var localWorld = RequiredIndex(source, "var localWorld = await runtime.Storage.LoadWorldAsync(joined.WorldId);", liveLobby);
        var catchUp = RequiredIndex(source, "runtime.CatchUp.RequestCatchUpAsync(", localWorld);
        var nullableRevision = RequiredIndex(source, "localWorld?.CurrentStateRevisionId", catchUp);
        var synchronized = RequiredIndex(source, "RequireSynchronizedPeerWorldAsync(", nullableRevision);
        var refresh = RequiredIndex(source, "await RefreshUnifiedWorldsAsync(", synchronized);

        Assert.True(subscription < handler);
        Assert.True(handler < joinLobby);
        Assert.True(joinLobby < liveLobby);
        Assert.True(liveLobby < localWorld);
        Assert.True(localWorld < catchUp);
        Assert.True(catchUp < nullableRevision);
        Assert.True(nullableRevision < synchronized);
        Assert.True(synchronized < refresh);
    }

    [Fact]
    public void PeerJoinRefreshesCanonicalHeadBeforeEnvironmentAndRechecksLobbyBeforeLaunch()
    {
        var source = ReadJoin();
        var method = RequiredIndex(source, "private async Task JoinPeerWorldAsync(");
        var firstLobby = RequiredIndex(source, "var snapshot = await RequirePeerJoinLobbyAsync(runtime, world.Id);", method);
        var catchUp = RequiredIndex(source, "var catchUp = await runtime.CatchUp.RequestCatchUpAsync(", firstLobby);
        var synchronized = RequiredIndex(source, "var synchronized = await RequireSynchronizedPeerWorldAsync(", catchUp);
        var installation = RequiredIndex(source, "var installation = await GetReadyInstallationForWorldAsync(", synchronized);
        var launchLobby = RequiredIndex(source, "var launchSnapshot = await RequirePeerJoinLobbyAsync(", installation);
        var generationGuard = RequiredIndex(source, "launchSnapshot.AuthorityGeneration != catchUp.AuthorityGeneration", launchLobby);
        var bridge = RequiredIndex(source, "await using var bridge = await runtime.GameBridge.OpenClientAsync(", generationGuard);
        var coreJoin = RequiredIndex(source, "var join = new WorldJoinService(runtime.Storage);", bridge);
        var launch = RequiredIndex(source, "await join.JoinAsync(", coreJoin);
        var loopbackConnection = RequiredIndex(source, "bridge.GameConnection", launch);

        Assert.True(firstLobby < catchUp);
        Assert.True(catchUp < synchronized);
        Assert.True(synchronized < installation);
        Assert.True(installation < launchLobby);
        Assert.True(launchLobby < generationGuard);
        Assert.True(generationGuard < bridge);
        Assert.True(bridge < coreJoin);
        Assert.True(coreJoin < launch);
        Assert.True(launch < loopbackConnection);
    }

    [Fact]
    public void PeerGameBridgeLifetimeWrapsTheEntireCoreJoinSession()
    {
        var source = ReadJoin();
        var method = RequiredIndex(source, "private async Task JoinPeerWorldAsync(");
        var bridge = RequiredIndex(source, "await using var bridge = await runtime.GameBridge.OpenClientAsync(", method);
        var join = RequiredIndex(source, "await join.JoinAsync(", bridge);
        var leftStatus = RequiredIndex(source, "StatusText.Text = $\"Left hosted World", join);

        Assert.True(bridge < join);
        Assert.True(join < leftStatus);
        Assert.Contains("bridge.GameConnection", source[bridge..leftStatus], StringComparison.Ordinal);
    }

    [Fact]
    public void PeerReadinessUsesAttachedSteamLobbyRatherThanBackendHostPresence()
    {
        var source = ReadJoin();
        var peerBranch = RequiredIndex(
            source,
            "if (_peerWorldIds.Contains(world.Id))",
            RequiredIndex(source, "private async Task RefreshSelectedWorldHostPresenceAsync()"));
        var peerEnd = RequiredIndex(source, "var runtime = _remoteRuntime;", peerBranch);
        var peerSection = source[peerBranch..peerEnd];

        Assert.Contains("peerRuntime.Lobby.GetAsync(world.Id)", peerSection, StringComparison.Ordinal);
        Assert.DoesNotContain("GetHostPresenceAsync", peerSection, StringComparison.Ordinal);
        Assert.DoesNotContain("StewardRemoteHostPresence", peerSection, StringComparison.Ordinal);
        Assert.DoesNotContain("presence.Address", peerSection, StringComparison.Ordinal);
    }

    [Fact]
    public void PeerJoinRequiresStableConfirmedRemoteHostAndNeverJoinsItself()
    {
        var source = ReadJoin();
        var helper = RequiredIndex(source, "private async Task<PeerWorldLobbySnapshot> RequirePeerJoinLobbyAsync(");
        var confirmed = RequiredIndex(source, "!snapshot.OwnerConfirmed", helper);
        var generation = RequiredIndex(source, "snapshot.AuthorityGeneration == 0", confirmed);
        var handoff = RequiredIndex(source, "snapshot.RequestedHost is not null", generation);
        var self = RequiredIndex(source, "SamePeerIdentity(snapshot.Owner, runtime.User)", handoff);

        Assert.True(confirmed < generation);
        Assert.True(generation < handoff);
        Assert.True(handoff < self);
    }

    [Fact]
    public void PeerJoinKeepsFriendsOnlyInviteModelAndDoesNotAddSearchableLobbyDiscovery()
    {
        var joinSource = ReadJoin();
        var lobbySource = Read("src/SharedWorlds.Desktop/SteamPeerWorldLobby.cs");

        Assert.Contains("LobbyJoin.JoinRequested", joinSource, StringComparison.Ordinal);
        Assert.Contains("ELobbyType.k_ELobbyTypeFriendsOnly", lobbySource, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestLobbyList", joinSource, StringComparison.Ordinal);
        Assert.DoesNotContain("k_ELobbyTypePublic", joinSource, StringComparison.Ordinal);
        Assert.DoesNotContain("k_ELobbyTypeInvisible", joinSource, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyRemoteJoinRemainsBelowPeerBranchDuringMigration()
    {
        var source = ReadJoin();
        var peerJoin = RequiredIndex(source, "await JoinPeerWorldAsync(world, peerAdapter);");
        var remoteRuntime = RequiredIndex(source, "var runtime = _remoteRuntime;", peerJoin);
        var remotePresence = RequiredIndex(source, "runtime.GetHostPresenceAsync(world.Id)", remoteRuntime);
        var remoteJoin = RequiredIndex(source, "await runtime.Join.JoinAsync(", remotePresence);

        Assert.True(peerJoin < remoteRuntime);
        Assert.True(remoteRuntime < remotePresence);
        Assert.True(remotePresence < remoteJoin);
    }

    private static string ReadJoin()
        => Read("src/SharedWorlds.Desktop/MainWindow.WorldJoin.cs");

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
