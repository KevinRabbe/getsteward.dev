using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PeerInitialShareCutoverCompositionTests
{
    [Fact]
    public void RuntimeExposesFreshGenerationOneShareService()
    {
        var source = Read("src/SharedWorlds.Desktop/StewardDesktopPeerRuntime.cs");
        var service = RequiredIndex(source, "var initialShare = new PeerWorldInitialShareService(");
        var serviceEnd = RequiredIndex(source, "authorityFences);", service);

        Assert.Contains("var membership = new PeerWorldMembershipService(", source, StringComparison.Ordinal);
        Assert.Contains("public PeerWorldInitialShareService InitialShare { get; }", source, StringComparison.Ordinal);
        Assert.Contains("storage,\n                authorityFences);", source[service..(serviceEnd + "authorityFences);".Length)], StringComparison.Ordinal);
    }

    [Fact]
    public void FreshLocalOnlyShareBranchesBeforeAnyLegacyRemotePublisherPath()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.WorldSharing.cs");
        var localOnly = RequiredIndex(source, "if (world.SharingMode == WorldSharingMode.LocalOnly)");
        var peerRuntime = RequiredIndex(source, "var peerShareRuntime = _peerRuntime;", localOnly);
        var initialShare = RequiredIndex(source, "peerShareRuntime.InitialShare.ShareAsync(", peerRuntime);
        var legacyComment = RequiredIndex(source, "only pre-peer legacy Shared transactions are allowed", initialShare);
        var remoteRuntime = RequiredIndex(source, "var remoteRuntime = _remoteRuntime;", legacyComment);
        var publisher = RequiredIndex(source, "remoteRuntime.InitialWorldPublisher.PublishAsync(", remoteRuntime);

        Assert.True(localOnly < peerRuntime);
        Assert.True(peerRuntime < initialShare);
        Assert.True(initialShare < legacyComment);
        Assert.True(legacyComment < remoteRuntime);
        Assert.True(remoteRuntime < publisher);
    }

    [Fact]
    public void FreshPeerShareKeepsExactEnvironmentPreflightBeforeAuthorityMutation()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.WorldSharing.cs");
        var localOnly = RequiredIndex(source, "if (world.SharingMode == WorldSharingMode.LocalOnly)");
        var selection = RequiredIndex(source, "SelectInstallationForWorldAsync(local, adapter)", localOnly);
        var ready = RequiredIndex(source, "if (!selection.Verification.IsReady)", selection);
        var share = RequiredIndex(source, "peerShareRuntime.InitialShare.ShareAsync(", ready);

        Assert.True(localOnly < selection);
        Assert.True(selection < ready);
        Assert.True(ready < share);
    }

    [Fact]
    public void FreshPeerShareBlockContainsNoBackendPublicationOrRemoteRuntimeDependency()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.WorldSharing.cs");
        var localOnly = RequiredIndex(source, "if (world.SharingMode == WorldSharingMode.LocalOnly)");
        var legacyComment = RequiredIndex(source, "only pre-peer legacy Shared transactions are allowed", localOnly);
        var block = source[localOnly..legacyComment];

        Assert.Contains("peerShareRuntime.InitialShare.ShareAsync", block, StringComparison.Ordinal);
        Assert.DoesNotContain("_remoteRuntime", block, StringComparison.Ordinal);
        Assert.DoesNotContain("InitialWorldPublisher", block, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenRevisionAsync", block, StringComparison.Ordinal);
        Assert.DoesNotContain("SharingMode = WorldSharingMode.Shared", block, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyPublisherRejectsPeerAuthorityAndAcceptsOnlyExistingSharedWorld()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.WorldSharing.cs");
        var legacyComment = RequiredIndex(source, "only pre-peer legacy Shared transactions are allowed");
        var sharedGate = RequiredIndex(source, "if (world.SharingMode != WorldSharingMode.Shared)", legacyComment);
        var localGate = RequiredIndex(source, "if (localShadow.SharingMode != WorldSharingMode.Shared ||", sharedGate);
        var peerReject = RequiredIndex(source, "localShadow.PeerAuthority is not null", localGate);
        var publisher = RequiredIndex(source, "remoteRuntime.InitialWorldPublisher.PublishAsync(", peerReject);

        Assert.True(legacyComment < sharedGate);
        Assert.True(sharedGate < localGate);
        Assert.True(localGate < peerReject);
        Assert.True(peerReject < publisher);
    }

    [Fact]
    public void FreshSharePresentationDependsOnPeerRuntimeNotBackend()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.WorldSharing.cs");
        var update = RequiredIndex(source, "private void UpdateWorldSharingActionState()");
        var localOnly = RequiredIndex(source, "if (world.SharingMode == WorldSharingMode.LocalOnly)", update);
        var legacyRetry = RequiredIndex(source, "var legacyRetry =", localOnly);
        var block = source[localOnly..legacyRetry];

        Assert.Contains("var available = _peerRuntime is not null;", block, StringComparison.Ordinal);
        Assert.Contains("No backend upload is required", block, StringComparison.Ordinal);
        Assert.DoesNotContain("_remoteRuntime", block, StringComparison.Ordinal);
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
