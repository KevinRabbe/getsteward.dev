using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class PeerRuntimeStartupCompositionTests
{
    [Fact]
    public void PeerRuntimeStartsAfterDurableDeviceIdentityAndBeforeRemoteSession()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.UnifiedStartup.cs");
        var deviceSettings = RequiredIndex(source, "await LoadDeviceSettingsAsync();");
        var peerRuntime = RequiredIndex(source, "InitializeStewardPeerRuntime();", deviceSettings);
        var remoteSession = RequiredIndex(source, "await InitializeStewardRemoteSessionAsync();", peerRuntime);

        Assert.True(deviceSettings < peerRuntime);
        Assert.True(peerRuntime < remoteSession);
    }

    [Fact]
    public void PeerStartupLoadsIndependentSteamConfigurationAndOneAppOwnedRuntime()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.PeerRuntime.cs");

        Assert.Contains("StewardDesktopSteamConfiguration.TryLoad(", source, StringComparison.Ordinal);
        Assert.Contains("app.TryGetOrCreateSteamPlatformRuntime(", source, StringComparison.Ordinal);
        Assert.Contains("configuration!.AppId", source, StringComparison.Ordinal);
        Assert.Contains("StewardDesktopPeerRuntime.Create(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StewardDesktopRemoteConfiguration", source, StringComparison.Ordinal);
        Assert.DoesNotContain("InitializeStewardRemoteSessionAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("StewardSessionClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamWebApiTicketSource", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PeerStartupRequiresDurableInstallationIdentityBeforeAuthorityComposition()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.PeerRuntime.cs");
        var durableGate = RequiredIndex(source, "if (!_deviceSettingsUsableForRemote)");
        var steamCreation = RequiredIndex(source, "app.TryGetOrCreateSteamPlatformRuntime(", durableGate);
        var peerCreation = RequiredIndex(source, "_peerRuntime = StewardDesktopPeerRuntime.Create(", steamCreation);

        Assert.True(durableGate < steamCreation);
        Assert.True(steamCreation < peerCreation);
        Assert.Contains("_deviceSettings.InstallationId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PeerStartupUsesRawCanonicalLocalStorageRootNotRemoteObservedWrapper()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.PeerRuntime.cs");

        Assert.Contains("CreatePeerCanonicalStorage()", source, StringComparison.Ordinal);
        Assert.Contains("new(Path.Combine(", source, StringComparison.Ordinal);
        Assert.Contains("GetLocalDataRoot(),\n            \"SharedWorlds\",\n            \"data\"", source, StringComparison.Ordinal);
        Assert.Contains("LocalWorldStorage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnedWorldLocationObservedWorldStorage", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnedWorldLocation", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PeerRuntimeLifetimeIsOwnedByMainWindowButSteamLifetimeIsNot()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.PeerRuntime.cs");

        Assert.Contains("Closed -= MainWindow_PeerRuntimeClosed;", source, StringComparison.Ordinal);
        Assert.Contains("Closed += MainWindow_PeerRuntimeClosed;", source, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _peerRuntime, null)?.Dispose();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("platform!.Dispose", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SteamAPI.Shutdown", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingPeerConfigurationDoesNotPreventLocalStartup()
    {
        var source = Read("src/SharedWorlds.Desktop/MainWindow.PeerRuntime.cs");
        var configurationLoad = RequiredIndex(source, "if (!StewardDesktopSteamConfiguration.TryLoad(");
        var firstReturn = RequiredIndex(source, "return;", configurationLoad);
        var peerCreation = RequiredIndex(source, "_peerRuntime = StewardDesktopPeerRuntime.Create(", firstReturn);

        Assert.True(configurationLoad < firstReturn);
        Assert.True(firstReturn < peerCreation);
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
