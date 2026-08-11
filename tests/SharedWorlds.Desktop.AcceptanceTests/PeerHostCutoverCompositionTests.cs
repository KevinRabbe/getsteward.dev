using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PeerHostCutoverCompositionTests
{
    [Fact]
    public void LocalPersistentPeerWorldsAreClassifiedBeforeRemoteWorldMerge()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.RemoteRuntime.cs");
        var localLoad = RequiredIndex(source, "var localWorlds = await _storage.ListWorldsAsync(cancellationToken);");
        var peerClear = RequiredIndex(source, "_peerWorldIds.Clear();", localLoad);
        var peerCondition = RequiredIndex(
            source,
            "localWorld.SharingMode == WorldSharingMode.Shared &&\n                localWorld.PeerAuthority is not null",
            peerClear);
        var peerAdd = RequiredIndex(source, "_peerWorldIds.Add(localWorld.Id);", peerCondition);
        var remoteLoad = RequiredIndex(source, "remoteWorlds = await remote.Storage.ListWorldsAsync", peerAdd);

        Assert.True(localLoad < peerClear);
        Assert.True(peerClear < peerCondition);
        Assert.True(peerCondition < peerAdd);
        Assert.True(peerAdd < remoteLoad);
    }

    [Fact]
    public void SameIdLegacyRemoteCopyCannotHideLocalPeerAuthorityWorld()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.RemoteRuntime.cs");
        var remoteLoop = RequiredIndex(source, "foreach (var remoteWorld in remoteWorlds)");
        var peerGuard = RequiredIndex(
            source,
            "if (_peerWorldIds.Contains(remoteWorld.Id))",
            remoteLoop);
        var guardContinue = RequiredIndex(source, "continue;", peerGuard);
        var remoteAdd = RequiredIndex(source, "_remoteWorldIds.Add(remoteWorld.Id);", guardContinue);

        Assert.True(remoteLoop < peerGuard);
        Assert.True(peerGuard < guardContinue);
        Assert.True(guardContinue < remoteAdd);
    }

    [Fact]
    public void PeerWorldStorageLifecycleAndIdentityResolveBeforeLegacyRemoteRuntime()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.RemoteRuntime.cs");

        AssertPeerFirst(source, "private IWorldStorage GetStorageForWorld(World world)", "RequirePeerRuntime(world).Storage");
        AssertPeerFirst(source, "private WorldLifecycleService GetLifecycleForWorld(World world)", "RequirePeerRuntime(world).Lifecycle");
        AssertPeerFirst(source, "private UserIdentity GetUserForWorld(World world)", "RequirePeerRuntime(world).User");
    }

    [Fact]
    public void PeerWorldAuthorityAvailabilityDependsOnEmbeddedRuntimeNotBackendRuntime()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.RemoteRuntime.cs");
        var method = RequiredIndex(source, "private bool HasAuthoritativeRuntimeForWorld(World world)");
        var peerCheck = RequiredIndex(source, "if (_peerWorldIds.Contains(world.Id))", method);
        var peerReturn = RequiredIndex(source, "return _peerRuntime is not null;", peerCheck);
        var remoteReturn = RequiredIndex(
            source,
            "return _remoteRuntime is not null && _remoteWorldIds.Contains(world.Id);",
            peerReturn);

        Assert.True(peerCheck < peerReturn);
        Assert.True(peerReturn < remoteReturn);
    }

    [Fact]
    public void MissingPeerRuntimeFailsClosedInsteadOfFallingBackToWritableLocalLifecycle()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.RemoteRuntime.cs");

        Assert.Contains("private StewardDesktopPeerRuntime RequirePeerRuntime(World world)", source, StringComparison.Ordinal);
        Assert.Contains("uses persistent peer authority, but the embedded Steam peer runtime is unavailable", source, StringComparison.Ordinal);
        Assert.Contains("A shared World can never fall back to unfenced local writable authority", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HostActionWrapsPeerAdapterBeforeAuthoritativeLifecycleLaunch()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.InstallationSelection.cs");
        var hostHandler = RequiredIndex(source, "private async void InstallationAwareHostButton_Click(");
        var lifecycle = RequiredIndex(source, "var lifecycle = GetLifecycleForWorld(world);", hostHandler);
        var wrapper = RequiredIndex(
            source,
            "var managedHostAdapter = GetManagedHostAdapterForWorld(world, adapter);",
            lifecycle);
        var launch = RequiredIndex(source, "var updated = await lifecycle.ContinueAsHostAsync(", wrapper);
        var wrappedArgument = RequiredIndex(source, "managedHostAdapter,", launch);

        Assert.True(hostHandler < lifecycle);
        Assert.True(lifecycle < wrapper);
        Assert.True(wrapper < launch);
        Assert.True(launch < wrappedArgument);
    }

    [Fact]
    public void ManagedHostWrapperIsAppliedOnlyToLocalPeerWorldClassification()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.RemoteRuntime.cs");
        var method = RequiredIndex(source, "private IGameAdapter GetManagedHostAdapterForWorld(");
        var peerClassification = RequiredIndex(source, "_peerWorldIds.Contains(world.Id)", method);
        var peerWrapper = RequiredIndex(
            source,
            "RequirePeerRuntime(world).CoordinateManagedHost(world.Id, adapter)",
            peerClassification);
        var fallback = RequiredIndex(source, ": adapter;", peerWrapper);

        Assert.True(peerClassification < peerWrapper);
        Assert.True(peerWrapper < fallback);
        Assert.DoesNotContain(
            "_remoteRuntime?.CoordinateManagedHost",
            source[method..],
            StringComparison.Ordinal);
    }

    private static void AssertPeerFirst(
        string source,
        string methodSignature,
        string peerResolution)
    {
        var method = RequiredIndex(source, methodSignature);
        var peerCheck = RequiredIndex(source, "if (_peerWorldIds.Contains(world.Id))", method);
        var peerValue = RequiredIndex(source, peerResolution, peerCheck);
        var remoteCheck = RequiredIndex(source, "if (_remoteWorldIds.Contains(world.Id))", peerValue);

        Assert.True(peerCheck < peerValue);
        Assert.True(peerValue < remoteCheck);
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
