using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PeerDefaultShellCompositionTests
{
    [Fact]
    public void PeerJoinInviteHandlingIsInitializedRegardlessOfLegacyRemoteRuntime()
    {
        var source = ReadStartup();
        var remoteSession = RequiredIndex(source, "await InitializeStewardRemoteSessionAsync();");
        var joinUi = RequiredIndex(source, "await InitializeWorldJoinUiAsync();", remoteSession);
        var coldJoin = RequiredIndex(source, "InitializeSteamLobbyLaunchRequest();", joinUi);
        var legacyGuard = RequiredIndex(source, "if (_remoteRuntime is not null)", coldJoin);

        Assert.True(remoteSession < joinUi);
        Assert.True(joinUi < coldJoin);
        Assert.True(coldJoin < legacyGuard);
    }

    [Fact]
    public void LegacyInvitationInboxIsCreatedOnlyForEstablishedRemoteMigrationRuntime()
    {
        var source = ReadStartup();
        var comment = RequiredIndex(source, "legacy backend invitation inbox belongs only to an actually-established migration runtime");
        var guard = RequiredIndex(source, "if (_remoteRuntime is not null)", comment);
        var initialize = RequiredIndex(source, "await InitializeWorldInvitationsUiAsync();", guard);
        var guardEnd = RequiredIndex(source, "}\n\n        InitializeProfessionalProductShell();", initialize);

        Assert.True(comment < guard);
        Assert.True(guard < initialize);
        Assert.True(initialize < guardEnd);
    }

    [Fact]
    public void LegacyInvitationRehomeIsAlsoRemoteRuntimeOnly()
    {
        var source = ReadStartup();
        var shell = RequiredIndex(source, "InitializeProfessionalProductShell();");
        var guard = RequiredIndex(source, "if (_remoteRuntime is not null)", shell);
        var rehome = RequiredIndex(source, "RehomeInvitationsToGlobalLobby();", guard);
        var visual = RequiredIndex(source, "InitializeVisualDesignV2();", rehome);

        Assert.True(shell < guard);
        Assert.True(guard < rehome);
        Assert.True(rehome < visual);
    }

    [Fact]
    public void NormalPeerShellCannotInitializeLegacyInboxBeforeRemoteSessionDecision()
    {
        var source = ReadStartup();
        var remoteSession = RequiredIndex(source, "await InitializeStewardRemoteSessionAsync();");
        var invitationInit = RequiredIndex(source, "await InitializeWorldInvitationsUiAsync();");

        Assert.True(remoteSession < invitationInit);
        var preRemote = source[..remoteSession];
        Assert.DoesNotContain("InitializeWorldInvitationsUiAsync", preRemote, StringComparison.Ordinal);
        Assert.DoesNotContain("RehomeInvitationsToGlobalLobby", preRemote, StringComparison.Ordinal);
    }

    private static string ReadStartup()
        => File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/MainWindow.UnifiedStartup.cs"));

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
