using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PeerWorldAccessCutoverCompositionTests
{
    [Fact]
    public void ManageAccessRoutesPersistentPeerWorldBeforeLegacyRemoteWorld()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.WorldSharing.cs");
        var click = RequiredIndex(source, "private async void ShareWorldButton_Click");
        var peer = RequiredIndex(source, "if (_peerWorldIds.Contains(world.Id))", click);
        var peerDialog = RequiredIndex(source, "new PeerWorldAccessDialog(peer, canonical)", peer);
        var remote = RequiredIndex(source, "if (_remoteWorldIds.Contains(world.Id))", peerDialog);
        var legacyDialog = RequiredIndex(source, "new WorldAccessDialog(", remote);

        Assert.True(click < peer);
        Assert.True(peer < peerDialog);
        Assert.True(peerDialog < remote);
        Assert.True(remote < legacyDialog);
    }

    [Fact]
    public void PeerAccessDoesNotRequireLegacyBackendRuntime()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.WorldSharing.cs");
        var peer = RequiredIndex(source, "if (_peerWorldIds.Contains(world.Id))");
        var remote = RequiredIndex(source, "if (_remoteWorldIds.Contains(world.Id))", peer);
        var peerBlock = source[peer..remote];

        Assert.Contains("var peer = _peerRuntime;", peerBlock, StringComparison.Ordinal);
        Assert.Contains("peer.Storage.LoadWorldAsync(world.Id)", peerBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("_remoteRuntime", peerBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("remote.Access", peerBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void PeerDialogPersistsMembershipBeforeTryingInvitationDelivery()
    {
        var source = Read("src/SharedWorlds.Desktop/PeerWorldAccessDialog.cs");
        var add = RequiredIndex(source, "private async void AddButton_Click");
        var membership = RequiredIndex(source, "_runtime.Membership.AddMemberAsync(", add);
        var invitations = RequiredIndex(source, "_runtime.Invitations.InviteCanonicalMembersAsync(", membership);

        Assert.True(add < membership);
        Assert.True(membership < invitations);
        Assert.Contains("Membership is already canonical. Host Ready will retry", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PeerDialogUsesCanonicalStorageAndOnlyCurrentAuthorityHolderCanAdd()
    {
        var source = Read("src/SharedWorlds.Desktop/PeerWorldAccessDialog.cs");
        var reload = RequiredIndex(source, "private async Task ReloadAsync");
        var canonical = RequiredIndex(source, "_runtime.Storage.LoadWorldAsync(_world.Id)", reload);
        var authority = RequiredIndex(source, "_world.PeerAuthority is { } authority", canonical);
        var holder = RequiredIndex(source, "SameUser(authority.Holder, _runtime.User)", authority);
        var enable = RequiredIndex(source, "_addButton.IsEnabled = !_busy && canManage;", holder);

        Assert.True(reload < canonical);
        Assert.True(canonical < authority);
        Assert.True(authority < holder);
        Assert.True(holder < enable);
    }

    [Fact]
    public void PeerDialogDoesNotPretendUnsupportedRevocationTransferOrLeaveSemanticsExist()
    {
        var source = Read("src/SharedWorlds.Desktop/PeerWorldAccessDialog.cs");

        Assert.DoesNotContain("RevokeMember", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveAccess", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TransferAccessManager", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MakeAccessManager", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LeaveWorld", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StewardWorldAccessClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SharedWorlds.Infrastructure.Remote", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ManageAccessPresentationNoLongerNeedsBackendForPeerWorld()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.WorldSharing.cs");
        var update = RequiredIndex(source, "private void UpdateWorldSharingActionState()");
        var peer = RequiredIndex(source, "if (_peerWorldIds.Contains(world.Id))", update);
        var remote = RequiredIndex(source, "if (_remoteWorldIds.Contains(world.Id))", peer);
        var peerBlock = source[peer..remote];

        Assert.Contains("DesktopText.ManageAccess", peerBlock, StringComparison.Ordinal);
        Assert.Contains("var available = _peerRuntime is not null;", peerBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("_remoteRuntime", peerBlock, StringComparison.Ordinal);
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
