using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PeerWorldLeaveCompositionTests
{
    [Fact]
    public void LeaveRequestCannotNameAnotherMember()
    {
        var source = ReadRepositoryFile(
            "src/SharedWorlds.Infrastructure/Sessions/PeerWorldLeave.cs");
        var request = RequiredIndex(source, "public sealed record PeerWorldLeaveRequest(");
        var result = RequiredIndex(source, "public sealed record PeerWorldLeaveResult(", request);
        var requestRecord = source[request..result];

        Assert.Contains("WorldId WorldId", requestRecord, StringComparison.Ordinal);
        Assert.Contains("ulong AuthorityGeneration", requestRecord, StringComparison.Ordinal);
        Assert.DoesNotContain("UserIdentity", requestRecord, StringComparison.Ordinal);
    }

    [Fact]
    public void Port71DerivesLeaveTargetFromAuthenticatedSteamConnection()
    {
        var source = ReadRepositoryFile(
            "src/SharedWorlds.Desktop/SteamPeerWorldRevisionExchange.cs");

        Assert.Contains("LeaveRequest = 9", source, StringComparison.Ordinal);
        Assert.Contains("LeaveResult = 10", source, StringComparison.Ordinal);
        var handler = RequiredIndex(source, "private async Task HandleLeaveRequestAsync(");
        var remoteId = RequiredIndex(source, "context.RemoteSteamId.ToString", handler);
        var remoteUser = RequiredIndex(source, "var remoteUser = new UserIdentity(", remoteId);
        var route = RequiredIndex(source, "_leaveRouter.HandleAsync(", remoteUser);
        var result = RequiredIndex(source, "MessageKind.LeaveResult", route);

        Assert.True(handler < remoteId);
        Assert.True(remoteId < remoteUser);
        Assert.True(remoteUser < route);
        Assert.True(route < result);
    }

    [Fact]
    public void LeaveControlSurvivesItsOwnRevocationUntilAcknowledgement()
    {
        var source = ReadRepositoryFile(
            "src/SharedWorlds.Desktop/SteamPeerWorldRevisionExchange.cs");
        var handler = RequiredIndex(source, "private async Task HandleLeaveRequestAsync(");
        var nextMethod = RequiredIndex(source, "private async Task AuthorizeIncomingOfferAsync(", handler);
        var body = source[handler..nextMethod];

        Assert.Contains("_leaveRouter.HandleAsync(", body, StringComparison.Ordinal);
        Assert.Contains("context.Incoming!.Cancellation.Token", body, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCancellationToken", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_liveRevocations", body, StringComparison.Ordinal);
    }

    [Fact]
    public void RequesterDeletesReplicaOnlyAfterCanonicalAcknowledgement()
    {
        var source = ReadRepositoryFile(
            "src/SharedWorlds.Desktop/PeerWorldLeaveService.cs");

        var request = RequiredIndex(source, "await _requests.RequestLeaveAsync(");
        var validate = RequiredIndex(source, "canonicalResult.WorldId != worldId", request);
        var independentCleanup = RequiredIndex(source, "var cleanupToken = CancellationToken.None;", validate);
        var delete = RequiredIndex(source, "await _storage.DeleteWorldAsync(worldId, cleanupToken);", independentCleanup);
        var verify = RequiredIndex(source, "await _storage.LoadWorldAsync(worldId, cleanupToken)", delete);
        var lobby = RequiredIndex(source, "await _lobby.LeaveJoinedLobbyAsync(worldId, cleanupToken);", verify);

        Assert.True(request < validate);
        Assert.True(validate < independentCleanup);
        Assert.True(independentCleanup < delete);
        Assert.True(delete < verify);
        Assert.True(verify < lobby);
    }

    [Fact]
    public void ParticipantLobbyCleanupCannotDetachCurrentObservedOwner()
    {
        var source = ReadRepositoryFile(
            "src/SharedWorlds.Desktop/SteamPeerWorldLobby.ParticipantLeave.cs");

        var method = RequiredIndex(source, "public async Task LeaveJoinedLobbyAsync(");
        var snapshot = RequiredIndex(source, "var current = ReadSnapshot(worldId, lobbyId);", method);
        var ownerGuard = RequiredIndex(source, "SameUser(current.Owner, _platform.LocalUser)", snapshot);
        var leave = RequiredIndex(source, "SteamMatchmaking.LeaveLobby(lobbyId);", ownerGuard);
        var forget = RequiredIndex(source, "_knownLobbies.Remove(worldId);", leave);

        Assert.True(method < snapshot);
        Assert.True(snapshot < ownerGuard);
        Assert.True(ownerGuard < leave);
        Assert.True(leave < forget);
    }

    [Fact]
    public void AccessDialogEnablesLeaveOnlyForExactLiveNonHolderAuthority()
    {
        var source = ReadRepositoryFile(
            "src/SharedWorlds.Desktop/PeerWorldAccessDialog.cs");

        Assert.Contains("Content = \"Leave World\"", source, StringComparison.Ordinal);
        Assert.Contains("!SameUser(authority.Holder, _runtime.User)", source, StringComparison.Ordinal);
        Assert.Contains("var liveLobby = await _runtime.Lobby.GetAsync(_world.Id);", source, StringComparison.Ordinal);
        Assert.Contains("liveLobby.OwnerConfirmed", source, StringComparison.Ordinal);
        Assert.Contains("liveLobby.AuthorityGeneration == authority!.Generation", source, StringComparison.Ordinal);
        Assert.Contains("liveLobby.RequestedHost is null", source, StringComparison.Ordinal);
        Assert.Contains("SameUser(liveLobby.Owner, authority.Holder)", source, StringComparison.Ordinal);
        Assert.Contains("WorldWasLeft = true;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowDropsSelectionAfterPeerWorldWasLeft()
    {
        var source = ReadRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.WorldSharing.cs");
        var dialog = RequiredIndex(source, "var dialog = new PeerWorldAccessDialog(peer, canonical)");
        var left = RequiredIndex(source, "if (dialog.WorldWasLeft)", dialog);
        var refresh = RequiredIndex(source, "selectedWorldId: null", left);
        var warning = RequiredIndex(source, "dialog.LeaveWarning", refresh);

        Assert.True(dialog < left);
        Assert.True(left < refresh);
        Assert.True(refresh < warning);
    }

    [Fact]
    public void PeerLeavePathDoesNotDependOnLegacyBackendRuntime()
    {
        var leave = ReadRepositoryFile(
            "src/SharedWorlds.Desktop/PeerWorldLeaveService.cs");
        var dialog = ReadRepositoryFile(
            "src/SharedWorlds.Desktop/PeerWorldAccessDialog.cs");

        Assert.DoesNotContain("_remoteRuntime", leave, StringComparison.Ordinal);
        Assert.DoesNotContain("SharedWorlds.Infrastructure.Remote", leave, StringComparison.Ordinal);
        Assert.DoesNotContain("StewardDesktopRemoteRuntime", leave, StringComparison.Ordinal);
        Assert.DoesNotContain("_remoteRuntime", dialog, StringComparison.Ordinal);
    }

    private static int RequiredIndex(string source, string value, int startIndex = 0)
    {
        var index = source.IndexOf(value, startIndex, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Required source fragment was not found: {value}");
        return index;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var candidate = Path.Combine(workspace, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }
}
