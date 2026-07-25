using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.PlanetCrafter.Tests;

public sealed class PlanetCrafterAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-planet-crafter-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];

    [Fact]
    public void InstallationDiscoveryFindsSteamInstallAndSaveRoot()
    {
        var library = Path.Combine(_root, "steam-library");
        var installRoot = Path.Combine(
            library,
            "steamapps",
            "common",
            "The Planet Crafter");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Planet Crafter.exe"), [1]);
        var manifest = Path.Combine(
            library,
            "steamapps",
            "appmanifest_1284190.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "\"AppState\" { \"buildid\" \"12345\" }");
        var worldRoot = Path.Combine(
            _root,
            "LocalLow",
            "MijuGames",
            "Planet Crafter");

        var installation = Assert.Single(
            PlanetCrafterInstallationDiscovery.DiscoverFromSteamLibraries(
                [library],
                worldRoot));

        Assert.Equal(Path.GetFullPath(installRoot), installation.RootPath);
        Assert.Equal("steam", installation.Source);
        Assert.NotNull(installation.Metadata);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "Planet Crafter.exe")),
            installation.Metadata[PlanetCrafterInstallationDiscovery.ClientExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(manifest),
            installation.Metadata[PlanetCrafterInstallationDiscovery.SteamManifestPathKey]);
        Assert.Equal(
            Path.GetFullPath(worldRoot),
            installation.Metadata[PlanetCrafterInstallationDiscovery.WorldRootPathKey]);
        Assert.Equal(
            "1284190",
            installation.Metadata[PlanetCrafterInstallationDiscovery.GameSteamAppIdKey]);
    }

    [Fact]
    public void WorldDiscoveryFindsCurrentSavesAndExcludesBackupAndEmptyFiles()
    {
        var worldRoot = Path.Combine(_root, "worlds");
        Directory.CreateDirectory(worldRoot);
        File.WriteAllBytes(Path.Combine(worldRoot, "Standard-1.json"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(worldRoot, "Custom-3.json"), [4, 5]);
        File.WriteAllBytes(Path.Combine(worldRoot, "Backup.json"), [9, 9]);
        File.WriteAllBytes(Path.Combine(worldRoot, "Broken.json"), []);
        File.WriteAllText(Path.Combine(worldRoot, "notes.txt"), "not a World");

        var worlds = PlanetCrafterWorldDiscovery.DiscoverFromWorldRoot(worldRoot);

        Assert.Equal(2, worlds.Count);
        Assert.Contains(worlds, world => world.DisplayName == "Standard-1");
        Assert.Contains(worlds, world => world.DisplayName == "Custom-3");
        Assert.DoesNotContain(worlds, world => world.DisplayName == "Backup");
        Assert.DoesNotContain(worlds, world => world.DisplayName == "Broken");
    }

    [Fact]
    public void WorldDiscoverySkipsLinkedSave()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var worldRoot = Path.Combine(_root, "linked-worlds");
        Directory.CreateDirectory(worldRoot);
        var outside = Path.Combine(_root, "outside.json");
        File.WriteAllBytes(outside, [1, 2, 3]);
        var linked = Path.Combine(worldRoot, "Standard-1.json");
        File.CreateSymbolicLink(linked, outside);
        try
        {
            Assert.Empty(PlanetCrafterWorldDiscovery.DiscoverFromWorldRoot(worldRoot));
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

        var environment = PlanetCrafterEnvironment.Inspect(installation);

        Assert.Equal(1, environment.SchemaVersion);
        Assert.Equal("planet-crafter", environment.AdapterId);
        Assert.Equal("24680", environment.GameVersion);
        Assert.Empty(environment.Components);
        Assert.Empty(environment.Configuration);
    }

    [Fact]
    public void EnvironmentInspectionRejectsBepInExBootstrapDll()
    {
        var installation = CreateInstallation("24680");
        File.WriteAllBytes(
            Path.Combine(installation.RootPath, "winhttp.dll"),
            [1]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PlanetCrafterEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentInspectionRejectsBepInExDirectoryEvenWithoutPlugins()
    {
        var installation = CreateInstallation("24680");
        Directory.CreateDirectory(Path.Combine(installation.RootPath, "BepInEx"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PlanetCrafterEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentInspectionRejectsDoorstopConfiguration()
    {
        var installation = CreateInstallation("24680");
        File.WriteAllText(
            Path.Combine(installation.RootPath, "doorstop_config.ini"),
            "enabled=true");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PlanetCrafterEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedSteamManifestFailsBeforeParsing()
    {
        var installation = CreateInstallation("12345");
        var manifest = installation.Metadata![PlanetCrafterInstallationDiscovery.SteamManifestPathKey];
        using (var stream = new FileStream(
                   manifest,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(PlanetCrafterEnvironment.MaximumSteamManifestBytes + 1);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PlanetCrafterEnvironment.Inspect(installation));

        Assert.Contains("metadata safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRestoreCaptureRoundTripPreservesOpaqueBytes()
    {
        var installation = CreateInstallation("13579");
        var environment = PlanetCrafterEnvironment.Inspect(installation);
        var source = Path.Combine(_root, "Standard-1.json");
        var expected = new byte[]
        {
            0x7B, 0x22, 0x69, 0x64, 0x22, 0x3A, 0x31, 0x7D,
            0x0A, 0x7B, 0x22, 0x70, 0x6C, 0x61, 0x79, 0x65,
            0x72, 0x22, 0x3A, 0x22, 0x68, 0x6F, 0x73, 0x74, 0x22,
            0x7D
        };
        File.WriteAllBytes(source, expected);
        var adapter = new PlanetCrafterAdapter();
        var detected = new DetectedWorld(
            "local:Standard-1",
            "Standard-1",
            source);

        var imported = await adapter.CaptureDetectedWorldAsync(
            installation,
            detected);
        _packages.Add(imported.Package.Path);
        Assert.Equal(expected, File.ReadAllBytes(imported.Package.Path));
        Assert.Equal(expected, File.ReadAllBytes(source));

        var prepared = await adapter.PrepareEnvironmentAsync(
            installation,
            environment);
        var workspace = prepared.WorkingDirectory;
        try
        {
            await adapter.RestoreStateAsync(prepared, imported.Package);
            var restored = Path.Combine(workspace, "save", "world.json");
            Assert.Equal(expected, File.ReadAllBytes(restored));

            var recaptured = await adapter.CaptureStateAsync(prepared);
            _packages.Add(recaptured.Package.Path);
            Assert.Equal(expected, File.ReadAllBytes(recaptured.Package.Path));

            await adapter.FinalizePreparedWorldAsync(
                prepared,
                PreparedWorldDisposition.Discard);
            Assert.False(Directory.Exists(workspace));
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                await adapter.FinalizePreparedWorldAsync(
                    prepared,
                    PreparedWorldDisposition.Discard);
            }
        }
    }

    [Fact]
    public async Task RestoreRejectsEmptyStatePackage()
    {
        var installation = CreateInstallation("13579");
        var environment = PlanetCrafterEnvironment.Inspect(installation);
        var emptyPackage = Path.Combine(_root, "empty.json");
        File.WriteAllBytes(emptyPackage, []);
        var adapter = new PlanetCrafterAdapter();
        var prepared = await adapter.PrepareEnvironmentAsync(
            installation,
            environment);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.RestoreStateAsync(
                    prepared,
                    new StatePackage("empty", emptyPackage)));

            Assert.Contains("empty", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await adapter.FinalizePreparedWorldAsync(
                prepared,
                PreparedWorldDisposition.Discard);
        }
    }

    [Fact]
    public async Task PrepareEnvironmentRejectsDifferentSteamBuild()
    {
        var installation = CreateInstallation("11111");
        var required = new EnvironmentManifest(
            1,
            "planet-crafter",
            "22222",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var adapter = new PlanetCrafterAdapter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.PrepareEnvironmentAsync(installation, required));

        Assert.Contains(
            "does not match required build",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterAdvertisesOnlyExactGameVersion()
    {
        var adapter = new PlanetCrafterAdapter();

        Assert.Equal(GameAdapterCapabilities.ExactGameVersion, adapter.Capabilities);
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var library = Path.Combine(_root, $"library-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(
            library,
            "steamapps",
            "common",
            "The Planet Crafter");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Planet Crafter.exe"), [1]);
        var manifest = Path.Combine(
            library,
            "steamapps",
            "appmanifest_1284190.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"buildid\" \"{buildId}\" }}");
        var worldRoot = Path.Combine(
            _root,
            $"worlds-{Guid.NewGuid():N}");

        return new GameInstallation(
            $"planet-crafter:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PlanetCrafterInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(
                    installRoot,
                    "Planet Crafter.exe"),
                [PlanetCrafterInstallationDiscovery.SteamManifestPathKey] = manifest,
                [PlanetCrafterInstallationDiscovery.WorldRootPathKey] = worldRoot,
                [PlanetCrafterInstallationDiscovery.GameSteamAppIdKey] = PlanetCrafterInstallationDiscovery.GameSteamAppId
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
