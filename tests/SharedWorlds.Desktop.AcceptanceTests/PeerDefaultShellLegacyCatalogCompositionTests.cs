using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PeerDefaultShellLegacyCatalogCompositionTests
{
    [Fact]
    public void OwnedPrivateCatalogPresentationHooksAndBringHereInitializeOnlyAfterRemoteSessionDecision()
    {
        var source = ReadStartup();
        var remoteSession = RequiredIndex(source, "await InitializeStewardRemoteSessionAsync();");
        var legacyComment = RequiredIndex(source, "Owned-private catalog and Bring Here are legacy backend reservation/location workflows.", remoteSession);
        var guard = RequiredIndex(source, "if (_remoteRuntime is not null)", legacyComment);
        var catalogUi = RequiredIndex(source, "InitializeOwnedPrivateWorldCatalogUi();", guard);
        var catalogHooks = RequiredIndex(source, "InitializeOwnedPrivateWorldCatalogRefreshHooks();", catalogUi);
        var bringHere = RequiredIndex(source, "InitializeOwnedPrivateWorldBringHereAction();", catalogHooks);
        var guardEnd = RequiredIndex(source, "}\n\n        UpdateWorldSharingActionState();", bringHere);

        Assert.True(remoteSession < legacyComment);
        Assert.True(legacyComment < guard);
        Assert.True(guard < catalogUi);
        Assert.True(catalogUi < catalogHooks);
        Assert.True(catalogHooks < bringHere);
        Assert.True(bringHere < guardEnd);
    }

    [Fact]
    public void PeerDefaultConstructorDoesNotBuildLegacyOwnedPrivateCatalogPresentation()
    {
        var source = ReadRepositoryFile("src/SharedWorlds.Desktop/MainWindow.xaml.cs");
        var constructor = RequiredIndex(source, "public MainWindow()");
        var constructorEnd = RequiredIndex(source, "private void InitializeLiveRegionAnnouncements()", constructor);
        var body = source[constructor..constructorEnd];

        Assert.DoesNotContain("InitializeOwnedPrivateWorldCatalogUi", body, StringComparison.Ordinal);
    }

    [Fact]
    public void PeerDefaultStartupCannotConstructOrRegisterLegacyOwnedLocationSurfaceBeforeRemoteRuntimeExists()
    {
        var source = ReadStartup();
        var remoteSession = RequiredIndex(source, "await InitializeStewardRemoteSessionAsync();");
        var preRemote = source[..remoteSession];

        Assert.DoesNotContain("InitializeOwnedPrivateWorldCatalogUi", preRemote, StringComparison.Ordinal);
        Assert.DoesNotContain("InitializeOwnedPrivateWorldCatalogRefreshHooks", preRemote, StringComparison.Ordinal);
        Assert.DoesNotContain("InitializeOwnedPrivateWorldBringHereAction", preRemote, StringComparison.Ordinal);
    }

    [Fact]
    public void PeerRuntimeAndCoreWorldUiRemainIndependentOfLegacyCatalogGuard()
    {
        var source = ReadStartup();
        var peerRuntime = RequiredIndex(source, "InitializeStewardPeerRuntime();");
        var gameUi = RequiredIndex(source, "await InitializeUnifiedGameUiAsync();", peerRuntime);
        var sharing = RequiredIndex(source, "InitializeWorldSharingUi();", gameUi);
        var remoteSession = RequiredIndex(source, "await InitializeStewardRemoteSessionAsync();", sharing);
        var join = RequiredIndex(source, "await InitializeWorldJoinUiAsync();", remoteSession);

        Assert.True(peerRuntime < gameUi);
        Assert.True(gameUi < sharing);
        Assert.True(sharing < remoteSession);
        Assert.True(remoteSession < join);
    }

    [Fact]
    public void LegacyCatalogGuardDoesNotGateSteamInviteJoinPath()
    {
        var source = ReadStartup();
        var bringHere = RequiredIndex(source, "InitializeOwnedPrivateWorldBringHereAction();");
        var join = RequiredIndex(source, "await InitializeWorldJoinUiAsync();", bringHere);
        var coldJoin = RequiredIndex(source, "InitializeSteamLobbyLaunchRequest();", join);

        Assert.True(bringHere < join);
        Assert.True(join < coldJoin);
    }

    private static string ReadStartup()
        => ReadRepositoryFile("src/SharedWorlds.Desktop/MainWindow.UnifiedStartup.cs");

    private static string ReadRepositoryFile(string relativePath)
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
