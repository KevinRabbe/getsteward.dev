using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class StewardDesktopPeerRuntimeCompositionTests
{
    [Fact]
    public void RuntimeReusesExactlyTheProcessOwnedSteamPlatform()
    {
        var source = ReadRuntime();

        Assert.Contains("SteamPlatformRuntime platform,", source, StringComparison.Ordinal);
        Assert.Contains("new SteamCloudPeerAuthorityFenceStore(\n            platform,", source, StringComparison.Ordinal);
        Assert.Contains("var lobby = new SteamPeerWorldLobby(platform);", source, StringComparison.Ordinal);
        Assert.Contains("revisionExchange = new SteamPeerWorldRevisionExchange(\n                platform,", source, StringComparison.Ordinal);
        Assert.Contains("lobbyJoin = new SteamPeerWorldLobbyJoinService(\n                platform,", source, StringComparison.Ordinal);
        Assert.Contains("gameBridge = new SteamPeerGameDatagramBridge(\n                platform,", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamAPI.Init", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamAPI.Shutdown", source, StringComparison.Ordinal);
        Assert.DoesNotContain("platform.Dispose", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_platform.Dispose", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeUsesOneFencedLocalStorageForAuthorityLifecycleAndTransfers()
    {
        var source = ReadRuntime();
        var fencedStorage = RequiredIndex(source, "var storage = new PeerAuthorityFencedWorldStorage(");
        var revisionInstaller = RequiredIndex(source, "var revisionInstaller = new PeerWorldRevisionReplicaInstaller(", fencedStorage);
        var bootstrapInstaller = RequiredIndex(source, "var bootstrapInstaller = new PeerWorldBootstrapInstaller(", revisionInstaller);
        var bootstrapFence = RequiredIndex(source, "authorityFences);", bootstrapInstaller);
        var observerInstaller = RequiredIndex(source, "var observerInstaller = new PeerWorldObserverSyncInstaller(", bootstrapFence);
        var coordinator = RequiredIndex(source, "var authorityCoordinator = new PeerWorldSessionCoordinator(", observerInstaller);
        var lifecycle = RequiredIndex(source, "var lifecycle = new WorldLifecycleService(", coordinator);

        Assert.True(fencedStorage < revisionInstaller);
        Assert.True(revisionInstaller < bootstrapInstaller);
        Assert.True(bootstrapInstaller < bootstrapFence);
        Assert.True(bootstrapFence < observerInstaller);
        Assert.True(observerInstaller < coordinator);
        Assert.True(coordinator < lifecycle);
        Assert.Contains("localStorage,\n            authorityFences,\n            user", source, StringComparison.Ordinal);
        Assert.Contains("storage,\n                user,\n                authorityFences", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeReusesCallerOwnedWritableSessionGate()
    {
        var source = ReadRuntime();

        Assert.Contains("ManagedWritableSessionGate managedSessionGate,", source, StringComparison.Ordinal);
        Assert.Contains("recoveryStore,\n                managedSessionGate,\n                lifecycleObserver", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new ManagedWritableSessionGate(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeComposesGenerationBoundControlPlaneAndProcessLocalPresence()
    {
        var source = ReadRuntime();

        Assert.Contains("new GenerationBoundPeerWorldExchange(", source, StringComparison.Ordinal);
        Assert.Contains("new PeerWorldRevisionTransferService(", source, StringComparison.Ordinal);
        Assert.Contains("new PeerWorldSessionCoordinator(", source, StringComparison.Ordinal);
        Assert.Contains("new PeerManagedHostPresenceRegistry();", source, StringComparison.Ordinal);
        Assert.Contains("new PeerManagedHostPresenceSessionCoordinator(", source, StringComparison.Ordinal);
        Assert.Contains("new PeerWorldBootstrapTransferService(", source, StringComparison.Ordinal);
        Assert.Contains("new GenerationBoundPeerWorldObserverSyncExchange(", source, StringComparison.Ordinal);
        Assert.Contains("new PeerWorldObserverSyncService(", source, StringComparison.Ordinal);
        Assert.Contains("new PeerWorldMembershipService(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeOwnsSeparateWorldTransferAndGameDataPlaneComponents()
    {
        var source = ReadRuntime();

        Assert.Contains("new SteamPeerWorldRevisionExchange(", source, StringComparison.Ordinal);
        Assert.Contains("new PeerGameDatagramBridgeAdmissionService(", source, StringComparison.Ordinal);
        Assert.Contains("new SteamPeerGameDatagramBridge(", source, StringComparison.Ordinal);
        Assert.Contains("public SteamPeerGameDatagramBridge GameBridge { get; }", source, StringComparison.Ordinal);
        Assert.Contains("_gameBridge.Dispose();", source, StringComparison.Ordinal);
        Assert.Contains("_lobbyJoin.Dispose();", source, StringComparison.Ordinal);
        Assert.Contains("_revisionExchange.Dispose();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeCoordinatesManagedHostThroughExistingAdapterWrapper()
    {
        var source = ReadRuntime();

        Assert.Contains("public IGameAdapter CoordinateManagedHost(", source, StringComparison.Ordinal);
        Assert.Contains("adapter is IManagedHostEndpointProvider endpointProvider", source, StringComparison.Ordinal);
        Assert.Contains("new CoordinatedHostGameAdapter(", source, StringComparison.Ordinal);
        Assert.Contains("SessionCoordinator,\n                worldId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeContainsNoCentralBackendOrRemoteHttpComposition()
    {
        var source = ReadRuntime();

        Assert.DoesNotContain("StewardDesktopRemoteRuntime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SharedWorlds.Infrastructure.Remote", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Postgre", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MinIO", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("S3", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Backend.Api", source, StringComparison.Ordinal);
    }

    private static string ReadRuntime()
        => File.ReadAllText(FindRepositoryFile(
            "src/SharedWorlds.Desktop/StewardDesktopPeerRuntime.cs"));

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
