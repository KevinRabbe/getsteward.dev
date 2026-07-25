using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.Satisfactory.Tests;

public sealed class SatisfactoryAdapterTests : IDisposable
{
    private const string SteamProfileId = "76561198000000000";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-satisfactory-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];

    [Fact]
    public void InstallationDiscoveryFindsSteamInstallAndOwnedRoots()
    {
        var library = Path.Combine(_root, "steam-library");
        var installRoot = Path.Combine(library, "steamapps", "common", "Satisfactory");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "FactoryGameSteam.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_526870.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "\"AppState\" { \"buildid\" \"12345\" }");
        var saveGamesRoot = Path.Combine(
            _root,
            "LocalAppData",
            "FactoryGame",
            "Saved",
            "SaveGames");

        var installation = Assert.Single(
            SatisfactoryInstallationDiscovery.DiscoverFromSteamLibraries(
                [library],
                saveGamesRoot));

        Assert.Equal(Path.GetFullPath(installRoot), installation.RootPath);
        Assert.Equal("steam", installation.Source);
        Assert.NotNull(installation.Metadata);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "FactoryGameSteam.exe")),
            installation.Metadata[SatisfactoryInstallationDiscovery.ClientExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(manifest),
            installation.Metadata[SatisfactoryInstallationDiscovery.SteamManifestPathKey]);
        Assert.Equal(
            Path.GetFullPath(saveGamesRoot),
            installation.Metadata[SatisfactoryInstallationDiscovery.SaveGamesRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "FactoryGame", "Mods")),
            installation.Metadata[SatisfactoryInstallationDiscovery.ModsRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                library,
                "steamapps",
                "workshop",
                "content",
                "526870")),
            installation.Metadata[SatisfactoryInstallationDiscovery.WorkshopContentRootPathKey]);
        Assert.Equal(
            "526870",
            installation.Metadata[SatisfactoryInstallationDiscovery.GameSteamAppIdKey]);
    }

    [Fact]
    public void WorldDiscoveryUsesOnlyCanonicalSteamProfilesAndCurrentSavFiles()
    {
        var saveGamesRoot = Path.Combine(_root, "save-games");
        var steamProfile = Path.Combine(saveGamesRoot, SteamProfileId);
        Directory.CreateDirectory(steamProfile);
        File.WriteAllBytes(Path.Combine(steamProfile, "Factory Alpha.sav"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(steamProfile, "Factory Empty.sav"), []);
        File.WriteAllText(Path.Combine(steamProfile, "notes.txt"), "not a World");

        var epicProfile = Path.Combine(
            saveGamesRoot,
            "0123456789abcdef0123456789abcdef");
        Directory.CreateDirectory(epicProfile);
        File.WriteAllBytes(Path.Combine(epicProfile, "Epic Factory.sav"), [4]);

        var nonCanonicalNumericProfile = Path.Combine(
            saveGamesRoot,
            "0" + SteamProfileId);
        Directory.CreateDirectory(nonCanonicalNumericProfile);
        File.WriteAllBytes(
            Path.Combine(nonCanonicalNumericProfile, "Alias.sav"),
            [5]);

        var backupRoot = Path.Combine(saveGamesRoot, "SaveGames_backup");
        Directory.CreateDirectory(backupRoot);
        File.WriteAllBytes(Path.Combine(backupRoot, "Backup.sav"), [6]);

        var blueprintsRoot = Path.Combine(saveGamesRoot, "blueprints");
        Directory.CreateDirectory(blueprintsRoot);
        File.WriteAllBytes(Path.Combine(blueprintsRoot, "Blueprint.sav"), [7]);

        var world = Assert.Single(
            SatisfactoryWorldDiscovery.DiscoverFromSaveGamesRoot(saveGamesRoot));

        Assert.Equal($"local:{SteamProfileId}:Factory Alpha", world.Id);
        Assert.Equal("Factory Alpha", world.DisplayName);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(steamProfile, "Factory Alpha.sav")),
            world.SourcePath);
    }

    [Fact]
    public void WorldDiscoverySkipsLinkedSave()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var saveGamesRoot = Path.Combine(_root, "linked-save-games");
        var steamProfile = Path.Combine(saveGamesRoot, SteamProfileId);
        Directory.CreateDirectory(steamProfile);
        var outside = Path.Combine(_root, "outside.sav");
        File.WriteAllBytes(outside, [1, 2, 3]);
        var linked = Path.Combine(steamProfile, "Linked.sav");
        File.CreateSymbolicLink(linked, outside);
        try
        {
            Assert.Empty(
                SatisfactoryWorldDiscovery.DiscoverFromSaveGamesRoot(saveGamesRoot));
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

        var environment = SatisfactoryEnvironment.Inspect(installation);

        Assert.Equal(1, environment.SchemaVersion);
        Assert.Equal("satisfactory", environment.AdapterId);
        Assert.Equal("24680", environment.GameVersion);
        Assert.Empty(environment.Components);
        Assert.Empty(environment.Configuration);
    }

    [Fact]
    public void EnvironmentInspectionRejectsInstalledSmlOrMods()
    {
        var installation = CreateInstallation("24680");
        var modsRoot = installation.Metadata![SatisfactoryInstallationDiscovery.ModsRootPathKey];
        var smlRoot = Path.Combine(modsRoot, "SML");
        Directory.CreateDirectory(smlRoot);
        File.WriteAllText(Path.Combine(smlRoot, "SML.uplugin"), "{}");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SatisfactoryEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentInspectionRejectsWorkshopContent()
    {
        var installation = CreateInstallation("24680");
        var workshopRoot = installation.Metadata![SatisfactoryInstallationDiscovery.WorkshopContentRootPathKey];
        var itemRoot = Path.Combine(workshopRoot, "1234567890");
        Directory.CreateDirectory(itemRoot);
        File.WriteAllBytes(Path.Combine(itemRoot, "content.bin"), [1]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SatisfactoryEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentInspectionRejectsLinkedModsDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var installation = CreateInstallation("24680");
        var modsRoot = installation.Metadata![SatisfactoryInstallationDiscovery.ModsRootPathKey];
        Directory.CreateDirectory(Path.GetDirectoryName(modsRoot)!);
        var outside = Path.Combine(_root, "outside-mods");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(modsRoot, outside);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                SatisfactoryEnvironment.Inspect(installation));

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
        var manifest = installation.Metadata![SatisfactoryInstallationDiscovery.SteamManifestPathKey];
        using (var stream = new FileStream(
                   manifest,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(SatisfactoryEnvironment.MaximumSteamManifestBytes + 1);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SatisfactoryEnvironment.Inspect(installation));

        Assert.Contains("metadata safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRestoreCaptureRoundTripPreservesOpaqueBytes()
    {
        var installation = CreateInstallation("13579");
        var environment = SatisfactoryEnvironment.Inspect(installation);
        var source = Path.Combine(_root, "Factory Alpha.sav");
        var expected = Enumerable.Range(0, 8192)
            .Select(index => (byte)(index % 251))
            .ToArray();
        File.WriteAllBytes(source, expected);
        var adapter = new SatisfactoryAdapter();
        var detected = new DetectedWorld(
            $"local:{SteamProfileId}:Factory Alpha",
            "Factory Alpha",
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
            var restored = Path.Combine(workspace, "save", "world.sav");
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
        var environment = SatisfactoryEnvironment.Inspect(installation);
        var emptyPackage = Path.Combine(_root, "empty.sav");
        File.WriteAllBytes(emptyPackage, []);
        var adapter = new SatisfactoryAdapter();
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
            "satisfactory",
            "22222",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var adapter = new SatisfactoryAdapter();

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
        var adapter = new SatisfactoryAdapter();

        Assert.Equal(GameAdapterCapabilities.ExactGameVersion, adapter.Capabilities);
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var library = Path.Combine(_root, $"library-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(library, "steamapps", "common", "Satisfactory");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "FactoryGameSteam.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_526870.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"buildid\" \"{buildId}\" }}");
        var saveGamesRoot = Path.Combine(
            _root,
            $"save-games-{Guid.NewGuid():N}");

        return new GameInstallation(
            $"satisfactory:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SatisfactoryInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(
                    installRoot,
                    "FactoryGameSteam.exe"),
                [SatisfactoryInstallationDiscovery.SteamManifestPathKey] = manifest,
                [SatisfactoryInstallationDiscovery.SaveGamesRootPathKey] = saveGamesRoot,
                [SatisfactoryInstallationDiscovery.ModsRootPathKey] = Path.Combine(
                    installRoot,
                    "FactoryGame",
                    "Mods"),
                [SatisfactoryInstallationDiscovery.WorkshopContentRootPathKey] = Path.Combine(
                    library,
                    "steamapps",
                    "workshop",
                    "content",
                    "526870"),
                [SatisfactoryInstallationDiscovery.GameSteamAppIdKey] = SatisfactoryInstallationDiscovery.GameSteamAppId
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
