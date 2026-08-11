using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PeerHostHandoffUiCompositionTests
{
    [Fact]
    public void LiveHandoffControlSharesHostedRunningBoundaryWithStopAndSave()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.ManagedHostStop.cs");
        var update = RequiredIndex(source, "private void UpdateManagedHostStopUi()");
        var canStop = RequiredIndex(source, "var canStop = adapterSupportsStop &&", update);
        var hosted = RequiredIndex(source, "responsibility.Mode == ManagedWorldSessionMode.Hosted", canStop);
        var running = RequiredIndex(source, "responsibility.Phase == WorldLifecyclePhase.Running", hosted);
        var canHandoff = RequiredIndex(source, "var canHandoff = canStop &&", running);
        var peer = RequiredIndex(source, "_peerWorldIds.Contains(world.Id)", canHandoff);
        var runtime = RequiredIndex(source, "_peerRuntime is not null", peer);

        Assert.True(update < canStop);
        Assert.True(canStop < hosted);
        Assert.True(hosted < running);
        Assert.True(running < canHandoff);
        Assert.True(canHandoff < peer);
        Assert.True(peer < runtime);
    }

    [Fact]
    public void HandoffLoadsCanonicalPeerWorldAndRequiresCurrentPersistentHolder()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.ManagedHostStop.cs");
        var handler = RequiredIndex(source, "private async void HandoffHostButton_Click");
        var canonical = RequiredIndex(source, "peer.Storage.LoadWorldAsync(world.Id)", handler);
        var authority = RequiredIndex(source, "canonical.PeerAuthority is not { } authority", canonical);
        var holder = RequiredIndex(source, "SameStableUser(authority.Holder, peer.User)", authority);
        var dialog = RequiredIndex(source, "new PeerHostHandoffDialog(canonical, peer.User)", holder);

        Assert.True(handler < canonical);
        Assert.True(canonical < authority);
        Assert.True(authority < holder);
        Assert.True(holder < dialog);
    }

    [Fact]
    public void DesktopRequestsLifecycleHandoffInsteadOfMutatingAuthorityDirectly()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.ManagedHostStop.cs");
        var handler = RequiredIndex(source, "private async void HandoffHostButton_Click");
        var target = RequiredIndex(source, "dialog.SelectedHost is not { } requestedHost", handler);
        var lifecycle = RequiredIndex(source, "var lifecycle = GetLifecycleForWorld(world);", target);
        var request = RequiredIndex(source, "lifecycle.RequestHostHandoffAsync(", lifecycle);

        Assert.True(handler < target);
        Assert.True(target < lifecycle);
        Assert.True(lifecycle < request);
        var handlerBlock = source[handler..RequiredIndex(source, "private void UpdateManagedHostStopUi()", request)];
        Assert.DoesNotContain("CompleteHandoffAsync", handlerBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("TransferOwnershipAsync", handlerBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveWorldAsync", handlerBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("PeerAuthority =", handlerBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void HandoffAndOrdinaryStopCannotBeRequestedAtTheSameTime()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.ManagedHostStop.cs");

        Assert.Contains("_hostStopRequestInFlight ||\n            _hostHandoffRequestInFlight", source, StringComparison.Ordinal);
        Assert.Contains("StopHostingButton.IsEnabled = canStop &&\n                                      !_hostStopRequestInFlight &&\n                                      !_hostHandoffRequestInFlight;", source, StringComparison.Ordinal);
        Assert.Contains("_handoffHostButton.IsEnabled = canHandoff &&\n                                           !_hostStopRequestInFlight &&\n                                           !_hostHandoffRequestInFlight;", source, StringComparison.Ordinal);
        Assert.Contains("_hostHandoffRequestInFlight = true;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HandoffButtonIsDynamicallyInsertedBesideExistingStopControl()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.ManagedHostStop.cs");
        var ensure = RequiredIndex(source, "private void EnsurePeerHostHandoffButton()");
        var parent = RequiredIndex(source, "StopHostingButton.Parent is not Panel playActions", ensure);
        var index = RequiredIndex(source, "playActions.Children.IndexOf(StopHostingButton)", parent);
        var insert = RequiredIndex(source, "playActions.Children.Insert(", index);

        Assert.True(ensure < parent);
        Assert.True(parent < index);
        Assert.True(index < insert);
    }

    [Fact]
    public void PickerOffersOnlyCanonicalRemoteSteamAuthorityCandidates()
    {
        var source = Read("src/SharedWorlds.Desktop/PeerHostHandoffDialog.cs");
        var members = RequiredIndex(source, "_candidates = world.Members");
        var excludeHolder = RequiredIndex(source, "!SameUser(member, localHolder)", members);
        var steamFilter = RequiredIndex(source, "IsSteamAuthorityCandidate(member)", excludeHolder);
        var provider = RequiredIndex(source, "string.Equals(member.Provider, \"steam\"", steamFilter);
        var numeric = RequiredIndex(source, "ulong.TryParse(", provider);
        var nonzero = RequiredIndex(source, "steamId != 0", numeric);

        Assert.True(members < excludeHolder);
        Assert.True(excludeHolder < steamFilter);
        Assert.True(steamFilter < provider);
        Assert.True(provider < numeric);
        Assert.True(numeric < nonzero);
        Assert.DoesNotContain("SteamMatchmaking", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PeerAuthority =", source, StringComparison.Ordinal);
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
