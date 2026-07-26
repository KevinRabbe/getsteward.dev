using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.ConanExilesEnhanced.Tests;

public sealed class ConanExilesEnhancedAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-conan-enhanced-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];

    [Fact]
    public void InstallationDiscoveryUsesManifestInstallDirInsteadOfHardcodedFolderName()
    {
        var library = Path.Combine(_root, "steam-library");
        var installName = "Conan Exiles Enhanced Test Name";
        var installRoot = Path.Combine(library, "steamapps", "common", installName);
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "ConanSandbox.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_440900.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"appid\" \"440900\" \"installdir\" \"{installName}\" \"buildid\" \"12345\" }}");

        var installation = Assert.Single(
            ConanExilesEnhancedInstallationDiscovery.DiscoverFromSteamLibraries([library]));

        Assert.Equal(Path.GetFullPath(installRoot), installation.RootPath);
        Assert.Equal("steam", installation.Source);
        Assert.NotNull(installation.Metadata);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "ConanSandbox.exe")),
            installation.Metadata[ConanExilesEnhancedInstallationDiscovery.ClientExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "ConanSandbox", "Saved")),
            installation.Metadata[ConanExilesEnhancedInstallationDiscovery.SaveGamesRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "ConanSandbox", "Mods", "modlist.txt")),
            installation.Metadata[ConanExilesEnhancedInstallationDiscovery.ModListPathKey]);
    }

    [Fact]
    public void WorldDiscoveryFindsOnlyTenEnhancedSaveSlotNames()
    {
        var saveRoot = Path.Combine(_root, "saved");
        Directory.CreateDirectory(saveRoot);
        File.WriteAllBytes(Path.Combine(saveRoot, "game_0.db"), [1]);
        File.WriteAllBytes(Path.Combine(saveRoot, "game_9.db"), [2]);
        File.WriteAllBytes(Path.Combine(saveRoot, "game.db"), [3]);
        File.WriteAllBytes(Path.Combine(saveRoot, "game_10.db"), [4]);
        File.WriteAllBytes(Path.Combine(saveRoot, "game_backup_1.db"), [5]);
        File.WriteAllBytes(Path.Combine(saveRoot, "other.db"), [6]);

        var worlds = ConanExilesEnhancedWorldDiscovery.DiscoverFromSavedRoot(saveRoot);

        Assert.Equal(2, worlds.Count);
        Assert.Contains(worlds, world => world.Id == "local:slot-0" && world.DisplayName == "Save slot 1");
        Assert.Contains(worlds, world => world.Id == "local:slot-9" && world.DisplayName == "Save slot 10");
    }

    [Fact]
    public void WorldDiscoveryRejectsSlotWithSqliteSidecar()
    {
        var saveRoot = Path.Combine(_root, "sidecar");
        Directory.CreateDirectory(saveRoot);
        var database = Path.Combine(saveRoot, "game_2.db");
        File.WriteAllBytes(database, [1, 2, 3]);
        File.WriteAllBytes(database + "-wal", [4]);

        Assert.Empty(ConanExilesEnhancedWorldDiscovery.DiscoverFromSavedRoot(saveRoot));
    }

    [Fact]
    public void WorldDiscoveryRejectsLinkedSlot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var saveRoot = Path.Combine(_root, "linked-save");
        Directory.CreateDirectory(saveRoot);
        var outside = Path.Combine(_root, "outside.db");
        File.WriteAllBytes(outside, [1, 2, 3]);
        var linked = Path.Combine(saveRoot, "game_0.db");
        File.CreateSymbolicLink(linked, outside);
        try
        {
            Assert.Empty(ConanExilesEnhancedWorldDiscovery.DiscoverFromSavedRoot(saveRoot));
        }
        finally
        {
            File.Delete(linked);
        }
    }

    [Fact]
    public void EnvironmentInspectionUsesExactSteamBuild()
    {
        var installation = CreateInstallation("24680");

        var environment = ConanExilesEnhancedEnvironment.Inspect(installation);

        Assert.Equal(1, environment.SchemaVersion);
        Assert.Equal("conan-exiles-enhanced", environment.AdapterId);
        Assert.Equal("24680", environment.GameVersion);
        Assert.Empty(environment.Components);
        Assert.Empty(environment.Configuration);
    }

    [Fact]
    public void EnvironmentInspectionRejectsNonEmptyModList()
    {
        var installation = CreateInstallation("24680");
        var modList = installation.Metadata![ConanExilesEnhancedInstallationDiscovery.ModListPathKey];
        Directory.CreateDirectory(Path.GetDirectoryName(modList)!);
        File.WriteAllText(modList, "*ExampleMod.pak\n");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConanExilesEnhancedEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentInspectionRejectsOversizedModList()
    {
        var installation = CreateInstallation("24680");
        var modList = installation.Metadata![ConanExilesEnhancedInstallationDiscovery.ModListPathKey];
        Directory.CreateDirectory(Path.GetDirectoryName(modList)!);
        using (var stream = new FileStream(modList, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(ConanExilesEnhancedEnvironment.MaximumModListBytes + 1);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConanExilesEnhancedEnvironment.Inspect(installation));

        Assert.Contains("metadata limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentInspectionRejectsLinkedModsDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var installation = CreateInstallation("24680");
        var modsRoot = installation.Metadata![ConanExilesEnhancedInstallationDiscovery.ModsRootPathKey];
        var outside = Path.Combine(_root, "outside-mods");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.GetDirectoryName(modsRoot)!);
        Directory.CreateSymbolicLink(modsRoot, outside);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                ConanExilesEnhancedEnvironment.Inspect(installation));
            Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(modsRoot);
        }
    }

    [Fact]
    public void OversizedSteamManifestFailsBeforeParsing()
    {
        var installation = CreateInstallation("12345");
        var manifest = installation.Metadata![ConanExilesEnhancedInstallationDiscovery.SteamManifestPathKey];
        using (var stream = new FileStream(manifest, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(ConanExilesEnhancedSteamManifest.MaximumBytes + 1);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConanExilesEnhancedEnvironment.Inspect(installation));

        Assert.Contains("metadata safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRestoreCapturePreservesOpaqueDatabaseBytesAndSource()
    {
        var installation = CreateInstallation("13579");
        var environment = ConanExilesEnhancedEnvironment.Inspect(installation);
        var saveRoot = installation.Metadata![ConanExilesEnhancedInstallationDiscovery.SaveGamesRootPathKey];
        Directory.CreateDirectory(saveRoot);
        var sourcePath = Path.Combine(saveRoot, "game_3.db");
        var bytes = Enumerable.Range(0, 16384)
            .Select(index => (byte)((index * 7) % 251))
            .ToArray();
        File.WriteAllBytes(sourcePath, bytes);
        var detected = Assert.Single(
            ConanExilesEnhancedWorldDiscovery.DiscoverFromSavedRoot(saveRoot));
        var adapter = new ConanExilesEnhancedAdapter();

        var imported = await adapter.CaptureDetectedWorldAsync(installation, detected);
        _packages.Add(imported.Package.Path);
        Assert.Equal(bytes, File.ReadAllBytes(imported.Package.Path));
        Assert.Equal(bytes, File.ReadAllBytes(sourcePath));

        var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);
        var workspace = prepared.WorkingDirectory;
        try
        {
            await adapter.RestoreStateAsync(prepared, imported.Package);
            var restored = Path.Combine(workspace, "save", "world.db");
            Assert.Equal(bytes, File.ReadAllBytes(restored));

            var recaptured = await adapter.CaptureStateAsync(prepared);
            _packages.Add(recaptured.Package.Path);
            Assert.Equal(bytes, File.ReadAllBytes(recaptured.Package.Path));

            await adapter.FinalizePreparedWorldAsync(prepared, PreparedWorldDisposition.Discard);
            Assert.False(Directory.Exists(workspace));
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                await adapter.FinalizePreparedWorldAsync(prepared, PreparedWorldDisposition.Discard);
            }
        }
    }

    [Fact]
    public async Task DirectCaptureRejectsDatabaseWithSqliteSidecar()
    {
        var installation = CreateInstallation("13579");
        var saveRoot = installation.Metadata![ConanExilesEnhancedInstallationDiscovery.SaveGamesRootPathKey];
        Directory.CreateDirectory(saveRoot);
        var sourcePath = Path.Combine(saveRoot, "game_0.db");
        File.WriteAllBytes(sourcePath, [1, 2, 3]);
        File.WriteAllBytes(sourcePath + "-journal", [4]);
        var adapter = new ConanExilesEnhancedAdapter();
        var detected = new DetectedWorld("local:slot-0", "Save slot 1", sourcePath);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.CaptureDetectedWorldAsync(installation, detected));

        Assert.Contains("idle current database", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreRejectsEmptyDatabasePackage()
    {
        var installation = CreateInstallation("13579");
        var environment = ConanExilesEnhancedEnvironment.Inspect(installation);
        var empty = Path.Combine(_root, "empty.db");
        File.WriteAllBytes(empty, []);
        var adapter = new ConanExilesEnhancedAdapter();
        var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.RestoreStateAsync(prepared, new StatePackage("empty", empty)));
        }
        finally
        {
            await adapter.FinalizePreparedWorldAsync(prepared, PreparedWorldDisposition.Discard);
        }
    }

    [Fact]
    public async Task PrepareEnvironmentRejectsDifferentSteamBuild()
    {
        var installation = CreateInstallation("11111");
        var required = new EnvironmentManifest(
            1,
            "conan-exiles-enhanced",
            "22222",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var adapter = new ConanExilesEnhancedAdapter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.PrepareEnvironmentAsync(installation, required));

        Assert.Contains("does not match required build", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestRejectsUnsafeInstallDirectory()
    {
        var manifest = Path.Combine(_root, "unsafe-manifest.acf");
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            manifest,
            "\"AppState\" { \"installdir\" \"..\\escape\" \"buildid\" \"12345\" }");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ConanExilesEnhancedSteamManifest.ReadRequired(manifest));

        Assert.Contains("safe library-relative", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterAdvertisesOnlyExactGameVersion()
    {
        var adapter = new ConanExilesEnhancedAdapter();
        Assert.Equal(GameAdapterCapabilities.ExactGameVersion, adapter.Capabilities);
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var library = Path.Combine(_root, $"library-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(library, "steamapps", "common", "Conan Exiles");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "ConanSandbox.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_440900.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"appid\" \"440900\" \"installdir\" \"Conan Exiles\" \"buildid\" \"{buildId}\" }}");
        var saveRoot = Path.Combine(installRoot, "ConanSandbox", "Saved");
        var modsRoot = Path.Combine(installRoot, "ConanSandbox", "Mods");

        return new GameInstallation(
            $"conan-exiles-enhanced:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ConanExilesEnhancedInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(
                    installRoot,
                    "ConanSandbox.exe"),
                [ConanExilesEnhancedInstallationDiscovery.SteamManifestPathKey] = manifest,
                [ConanExilesEnhancedInstallationDiscovery.SaveGamesRootPathKey] = saveRoot,
                [ConanExilesEnhancedInstallationDiscovery.ModsRootPathKey] = modsRoot,
                [ConanExilesEnhancedInstallationDiscovery.ModListPathKey] = Path.Combine(modsRoot, "modlist.txt"),
                [ConanExilesEnhancedInstallationDiscovery.GameSteamAppIdKey] = ConanExilesEnhancedInstallationDiscovery.GameSteamAppId
            });
    }

    public void Dispose()
    {
        foreach (var package in _packages)
        {
            TryDeleteFile(package);
        }

        TryDeleteDirectory(_root);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
