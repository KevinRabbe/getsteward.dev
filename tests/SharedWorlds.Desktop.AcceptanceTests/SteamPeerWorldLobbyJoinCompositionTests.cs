using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class SteamPeerWorldLobbyJoinCompositionTests
{
    [Fact]
    public void SteamJoinRequestIsPresentationSignalOnly()
    {
        var source = ReadJoinService();
        var callback = RequiredIndex(source, "private void OnGameLobbyJoinRequested(");
        var eventInvoke = RequiredIndex(source, "subscriber(request.m_steamIDLobby);", callback);

        Assert.Contains("public event Action<CSteamID>? JoinRequested;", source, StringComparison.Ordinal);
        Assert.True(callback < eventInvoke);
        Assert.DoesNotContain("JoinAsync(request.m_steamIDLobby", source, StringComparison.Ordinal);
        Assert.Contains("must not break Steam's callback pump", source, StringComparison.Ordinal);
    }

    [Fact]
    public void JoinValidatesStewardLobbyBeforeAttachingIt()
    {
        var source = ReadJoinService();
        var join = RequiredIndex(source, "public async Task<SteamJoinedWorldLobby> JoinAsync(");
        var schema = RequiredIndex(source, "SteamMatchmaking.GetLobbyData(lobbyId, SchemaKey)", join);
        var world = RequiredIndex(source, "Guid.TryParseExact(worldText, \"N\"", schema);
        var owner = RequiredIndex(source, "SteamMatchmaking.GetLobbyOwner(lobbyId)", world);
        var authority = RequiredIndex(source, "observedOwner.m_SteamID != authorityOwner", owner);
        var handoff = RequiredIndex(source, "SteamMatchmaking.GetLobbyData(lobbyId, RequestedHostKey)", authority);
        var attach = RequiredIndex(source, "await _lobby.AttachJoinedLobbyAsync(", handoff);

        Assert.True(schema < world);
        Assert.True(world < owner);
        Assert.True(owner < authority);
        Assert.True(authority < handoff);
        Assert.True(handoff < attach);
    }

    [Fact]
    public void FailedValidationLeavesSteamLobbyImmediately()
    {
        var source = ReadJoinService();
        var validationTry = RequiredIndex(source, "        try\n        {\n            var schema");
        var catchBlock = RequiredIndex(source, "        catch\n        {\n            SteamMatchmaking.LeaveLobby(lobbyId);", validationTry);

        Assert.True(validationTry < catchBlock);
    }

    [Fact]
    public void LobbyAdmissionStaysOnSharedSteamDispatcher()
    {
        var source = ReadJoinService();

        Assert.Contains("if (!_platform.Dispatcher.CheckAccess())", source, StringComparison.Ordinal);
        Assert.Contains("Steam lobby admission must run on Steward's Steam dispatcher.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamAPI.Init", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamAPI.Shutdown", source, StringComparison.Ordinal);
    }

    [Fact]
    public void JoinUsesSteamAsyncResultAndDoesNotAssumeRequestSucceeded()
    {
        var source = ReadJoinService();

        Assert.Contains("CallResult<LobbyEnter_t>.Create(", source, StringComparison.Ordinal);
        Assert.Contains("SteamMatchmaking.JoinLobby(lobbyId)", source, StringComparison.Ordinal);
        Assert.Contains("result.m_EChatRoomEnterResponse != SuccessfulLobbyEnterResponse", source, StringComparison.Ordinal);
        Assert.Contains("JoinTimeout", source, StringComparison.Ordinal);
    }

    private static string ReadJoinService()
        => File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/SteamPeerWorldLobbyJoinService.cs"));

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
