using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class DesktopLocalDataRootMigrationTests
{
    [Fact]
    public void FreshInstallCreatesSafeWorldRootAndBlocksLegacyRootRecreation()
    {
        using var temporary = new TemporaryDirectory();

        var resolved = DesktopLocalDataRoot.ResolveAndMigrate(temporary.Path);
        var legacyRoot = Path.Combine(temporary.Path, DesktopLocalDataRoot.LegacyDirectoryName);

        Assert.Equal(Path.Combine(temporary.Path, DesktopLocalDataRoot.CurrentDirectoryName), resolved);
        Assert.True(Directory.Exists(resolved));
        Assert.True(File.Exists(legacyRoot));
        Assert.False(Directory.Exists(legacyRoot));
        Assert.ThrowsAny<IOException>(
            () => Directory.CreateDirectory(Path.Combine(legacyRoot, "data")));

        Assert.Equal(resolved, DesktopLocalDataRoot.ResolveAndMigrate(temporary.Path));
    }

    [Fact]
    public void LegacyOnlyRootMovesWithoutCopyingOrLosingWorldAndSettingsBytes()
    {
        using var temporary = new TemporaryDirectory();
        var legacyRoot = Path.Combine(temporary.Path, DesktopLocalDataRoot.LegacyDirectoryName);
        var worldPath = Path.Combine(legacyRoot, "data", "worlds", "world-a", "world.json");
        var settingsPath = Path.Combine(legacyRoot, "settings", "device.json");
        Directory.CreateDirectory(Path.GetDirectoryName(worldPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(worldPath, "world-bytes");
        File.WriteAllText(settingsPath, "settings-bytes");

        var resolved = DesktopLocalDataRoot.ResolveAndMigrate(temporary.Path);

        Assert.Equal("world-bytes", File.ReadAllText(Path.Combine(
            resolved,
            "data",
            "worlds",
            "world-a",
            "world.json")));
        Assert.Equal("settings-bytes", File.ReadAllText(Path.Combine(
            resolved,
            "settings",
            "device.json")));
        Assert.True(File.Exists(legacyRoot));
        Assert.False(Directory.Exists(legacyRoot));
        Assert.ThrowsAny<IOException>(
            () => Directory.CreateDirectory(Path.Combine(legacyRoot, "data")));
    }

    [Fact]
    public void ExistingSafeWorldRootGetsLegacyGuardWithoutChangingItsContents()
    {
        using var temporary = new TemporaryDirectory();
        var currentRoot = Path.Combine(temporary.Path, DesktopLocalDataRoot.CurrentDirectoryName);
        var marker = Path.Combine(currentRoot, "data", "marker.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, "keep-me");

        var resolved = DesktopLocalDataRoot.ResolveAndMigrate(temporary.Path);

        Assert.Equal(currentRoot, resolved);
        Assert.Equal("keep-me", File.ReadAllText(marker));
        Assert.True(File.Exists(Path.Combine(
            temporary.Path,
            DesktopLocalDataRoot.LegacyDirectoryName)));
    }

    [Fact]
    public void TwoDirectoryRootsFailClosedAndRemainUntouched()
    {
        using var temporary = new TemporaryDirectory();
        var legacyRoot = Path.Combine(temporary.Path, DesktopLocalDataRoot.LegacyDirectoryName);
        var currentRoot = Path.Combine(temporary.Path, DesktopLocalDataRoot.CurrentDirectoryName);
        Directory.CreateDirectory(legacyRoot);
        Directory.CreateDirectory(currentRoot);
        File.WriteAllText(Path.Combine(legacyRoot, "legacy.txt"), "legacy");
        File.WriteAllText(Path.Combine(currentRoot, "current.txt"), "current");

        Assert.Throws<InvalidDataException>(
            () => DesktopLocalDataRoot.ResolveAndMigrate(temporary.Path));

        Assert.Equal("legacy", File.ReadAllText(Path.Combine(legacyRoot, "legacy.txt")));
        Assert.Equal("current", File.ReadAllText(Path.Combine(currentRoot, "current.txt")));
    }

    [Fact]
    public void ForeignLegacyFileFailsClosedInsteadOfBeingOverwritten()
    {
        using var temporary = new TemporaryDirectory();
        var legacyRoot = Path.Combine(temporary.Path, DesktopLocalDataRoot.LegacyDirectoryName);
        var currentRoot = Path.Combine(temporary.Path, DesktopLocalDataRoot.CurrentDirectoryName);
        Directory.CreateDirectory(currentRoot);
        File.WriteAllText(legacyRoot, "not-safeworlds-guard");

        Assert.Throws<InvalidDataException>(
            () => DesktopLocalDataRoot.ResolveAndMigrate(temporary.Path));

        Assert.Equal("not-safeworlds-guard", File.ReadAllText(legacyRoot));
        Assert.True(Directory.Exists(currentRoot));
    }

    [Fact]
    public void StartupAcquiresCrossVersionInstanceBoundaryBeforeMigratingOrConstructingStorage()
    {
        var appSource = Read("src/SharedWorlds.Desktop/App.xaml.cs");
        var singleInstance = RequiredIndex(
            appSource,
            "if (!TryBecomePrimaryDesktop(startupPortableWorldPath))");
        var migration = RequiredIndex(
            appSource,
            "DesktopLocalDataRoot.ResolveAndMigrateForCurrentUser();",
            singleInstance);
        var windowConstruction = RequiredIndex(appSource, "var window = new MainWindow", migration);

        Assert.True(singleInstance < migration);
        Assert.True(migration < windowConstruction);

        var coordinatorSource = Read(
            "src/SharedWorlds.Desktop/DesktopSingleInstanceCoordinator.cs");
        Assert.Contains(
            "DefaultInstanceName = \"SafeWorld.Desktop.Primary.v1\"",
            coordinatorSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "DefaultPipePrefix = \"SafeWorld.Desktop.Activation.v1\"",
            coordinatorSource,
            StringComparison.Ordinal);
        Assert.Contains("CurrentSessionOnly = true", coordinatorSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopPeerAndLegacyRemoteStateUseOnlyResolvedSafeWorldRoot()
    {
        var mainWindowSource = Read("src/SharedWorlds.Desktop/MainWindow.xaml.cs");
        var peerRuntimeSource = Read("src/SharedWorlds.Desktop/MainWindow.PeerRuntime.cs");
        var remoteAuthenticationSource = Read(
            "src/SharedWorlds.Desktop/MainWindow.RemoteAuthentication.cs");
        var remoteRuntimeSource = Read("src/SharedWorlds.Desktop/MainWindow.RemoteRuntime.cs");

        Assert.Contains(
            "DesktopLocalDataRoot.RequireResolvedRoot()",
            mainWindowSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"SharedWorlds\"", mainWindowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetLocalDataRoot", mainWindowSource, StringComparison.Ordinal);

        Assert.Contains("=> new(_storageRoot);", peerRuntimeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"SharedWorlds\"", peerRuntimeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetLocalDataRoot", peerRuntimeSource, StringComparison.Ordinal);

        Assert.Contains(
            "DesktopLocalDataRoot.RequireResolvedRoot()",
            remoteAuthenticationSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"SharedWorlds\"", remoteAuthenticationSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetLocalDataRoot", remoteAuthenticationSource, StringComparison.Ordinal);

        Assert.Contains(
            "DesktopLocalDataRoot.RequireResolvedRoot()",
            remoteRuntimeSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"SharedWorlds\"", remoteRuntimeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GetLocalDataRoot", remoteRuntimeSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopHasOneLocalApplicationDataResolverAndNoLegacyRootLiteralElsewhere()
    {
        var desktopRoot = FindRepositoryDirectory("src/SharedWorlds.Desktop");
        foreach (var path in Directory.EnumerateFiles(
                     desktopRoot,
                     "*.cs",
                     SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(
                    Path.GetFileName(path),
                    "DesktopLocalDataRoot.cs",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var source = File.ReadAllText(path);
            var relativePath = Path.GetRelativePath(desktopRoot, path);
            Assert.False(
                source.Contains("\"SharedWorlds\"", StringComparison.Ordinal),
                $"Legacy SharedWorlds local-root literal remains in '{relativePath}'.");
            Assert.False(
                source.Contains(
                    "Environment.SpecialFolder.LocalApplicationData",
                    StringComparison.Ordinal),
                $"Independent LocalApplicationData resolution remains in '{relativePath}'.");
            Assert.False(
                source.Contains("GetLocalDataRoot", StringComparison.Ordinal),
                $"Removed local-data helper remains in '{relativePath}'.");
        }
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

    private static string FindRepositoryDirectory(string relativePath)
    {
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var candidate = Path.Combine(workspace, relativePath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repository directory '{relativePath}'.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "safeworld-root-migration-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
