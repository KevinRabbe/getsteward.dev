using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.Astroneer.Tests;

public sealed class AstroneerAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-astroneer-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];

    [Fact]
    public void InstallationDiscoveryFindsSteamInstallAndOwnedRoots()
    {
        var library = Path.Combine(_root, "steam-library");
        var installRoot = Path.Combine(library, "steamapps", "common", "ASTRONEER");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Astro.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_361420.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "\"AppState\" { \"buildid\" \"12345\" }");
        var savedRoot = Path.Combine(_root, "Astro", "Saved");

        var installation = Assert.Single(
            AstroneerInstallationDiscovery.DiscoverFromSteamLibraries(
                [library],
                Path.Combine(savedRoot, "SaveGames"),
                Path.Combine(savedRoot, "Mods"),
                Path.Combine(savedRoot, "Paks")));

        Assert.Equal(Path.GetFullPath(installRoot), installation.RootPath);
        Assert.Equal("steam", installation.Source);
        Assert.NotNull(installation.Metadata);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "Astro.exe")),
            installation.Metadata[AstroneerInstallationDiscovery.ClientExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(manifest),
            installation.Metadata[AstroneerInstallationDiscovery.SteamManifestPathKey]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(savedRoot, "SaveGames")),
            installation.Metadata[AstroneerInstallationDiscovery.WorldRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(savedRoot, "Mods")),
            installation.Metadata[AstroneerInstallationDiscovery.ModsRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(savedRoot, "Paks")),
            installation.Metadata[AstroneerInstallationDiscovery.PaksRootPathKey]);
        Assert.Equal("361420", installation.Metadata[AstroneerInstallationDiscovery.GameSteamAppIdKey]);
    }

    [Fact]
    public void WorldDiscoveryIncludesOnlyNonEmptySavegames()
    {
        var worldRoot = Path.Combine(_root, "savegames");
        Directory.CreateDirectory(worldRoot);
        File.WriteAllBytes(Path.Combine(worldRoot, "Adventure.savegame"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(worldRoot, "Empty.savegame"), []);
        File.WriteAllBytes(Path.Combine(worldRoot, "PersistentLocalPlayerData.savecfg"), [4]);
        File.WriteAllText(Path.Combine(worldRoot, "notes.txt"), "not a World");

        var world = Assert.Single(AstroneerWorldDiscovery.DiscoverFromWorldRoot(worldRoot));

        Assert.Equal("local:Adventure", world.Id);
        Assert.Equal("Adventure", world.DisplayName);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(worldRoot, "Adventure.savegame")),
            world.SourcePath);
    }

    [Fact]
    public void WorldDiscoverySkipsLinkedSave()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var worldRoot = Path.Combine(_root, "linked-savegames");
        Directory.CreateDirectory(worldRoot);
        var outside = Path.Combine(_root, "outside.savegame");
        File.WriteAllBytes(outside, [1, 2, 3]);
        var linked = Path.Combine(worldRoot, "Linked.savegame");
        File.CreateSymbolicLink(linked, outside);
        try
        {
            Assert.Empty(AstroneerWorldDiscovery.DiscoverFromWorldRoot(worldRoot));
        }
        finally
        {
            File.Delete(linked);
        }
    }

    [Fact]
    public void EnvironmentInspectionUsesExactSteamBuild()
    {
        var environment = AstroneerEnvironment.Inspect(CreateInstallation("24680"));

        Assert.Equal(1, environment.SchemaVersion);
        Assert.Equal("astroneer", environment.AdapterId);
        Assert.Equal("24680", environment.GameVersion);
        Assert.Empty(environment.Components);
        Assert.Empty(environment.Configuration);
    }

    [Fact]
    public void EnvironmentInspectionRejectsModsDirectoryContent()
    {
        var installation = CreateInstallation("24680");
        var modsRoot = installation.Metadata![AstroneerInstallationDiscovery.ModsRootPathKey];
        Directory.CreateDirectory(modsRoot);
        File.WriteAllBytes(Path.Combine(modsRoot, "example.pak"), [1]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AstroneerEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentInspectionRejectsIntegratedPaksContent()
    {
        var installation = CreateInstallation("24680");
        var paksRoot = installation.Metadata![AstroneerInstallationDiscovery.PaksRootPathKey];
        Directory.CreateDirectory(paksRoot);
        File.WriteAllBytes(Path.Combine(paksRoot, "integrated.pak"), [1]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AstroneerEnvironment.Inspect(installation));

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
        var modsRoot = installation.Metadata![AstroneerInstallationDiscovery.ModsRootPathKey];
        Directory.CreateDirectory(Path.GetDirectoryName(modsRoot)!);
        var outside = Path.Combine(_root, "outside-mods");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(modsRoot, outside);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                AstroneerEnvironment.Inspect(installation));

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
        var manifest = installation.Metadata![AstroneerInstallationDiscovery.SteamManifestPathKey];
        using (var stream = new FileStream(
                   manifest,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(AstroneerEnvironment.MaximumSteamManifestBytes + 1);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AstroneerEnvironment.Inspect(installation));

        Assert.Contains("metadata safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRestoreCaptureRoundTripPreservesOpaqueBytes()
    {
        var installation = CreateInstallation("13579");
        var environment = AstroneerEnvironment.Inspect(installation);
        var source = Path.Combine(_root, "Adventure.savegame");
        var expected = Enumerable.Range(0, 8192)
            .Select(index => (byte)(index % 251))
            .ToArray();
        File.WriteAllBytes(source, expected);
        var adapter = new AstroneerAdapter();
        var detected = new DetectedWorld("local:Adventure", "Adventure", source);

        var imported = await adapter.CaptureDetectedWorldAsync(installation, detected);
        _packages.Add(imported.Package.Path);
        Assert.Equal(expected, File.ReadAllBytes(imported.Package.Path));
        Assert.Equal(expected, File.ReadAllBytes(source));

        var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);
        var workspace = prepared.WorkingDirectory;
        try
        {
            await adapter.RestoreStateAsync(prepared, imported.Package);
            var restored = Path.Combine(workspace, "save", "world.savegame");
            Assert.Equal(expected, File.ReadAllBytes(restored));

            var recaptured = await adapter.CaptureStateAsync(prepared);
            _packages.Add(recaptured.Package.Path);
            Assert.Equal(expected, File.ReadAllBytes(recaptured.Package.Path));

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
    public async Task RestoreRejectsEmptyStatePackage()
    {
        var installation = CreateInstallation("13579");
        var environment = AstroneerEnvironment.Inspect(installation);
        var emptyPackage = Path.Combine(_root, "empty.savegame");
        File.WriteAllBytes(emptyPackage, []);
        var adapter = new AstroneerAdapter();
        var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.RestoreStateAsync(prepared, new StatePackage("empty", emptyPackage)));

            Assert.Contains("empty", exception.Message, StringComparison.OrdinalIgnoreCase);
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
            "astroneer",
            "22222",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var adapter = new AstroneerAdapter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.PrepareEnvironmentAsync(installation, required));

        Assert.Contains("does not match required build", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterAdvertisesOnlyExactGameVersion()
    {
        Assert.Equal(
            GameAdapterCapabilities.ExactGameVersion,
            new AstroneerAdapter().Capabilities);
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var library = Path.Combine(_root, $"library-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(library, "steamapps", "common", "ASTRONEER");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Astro.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_361420.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"buildid\" \"{buildId}\" }}");
        var savedRoot = Path.Combine(_root, $"saved-{Guid.NewGuid():N}");

        return new GameInstallation(
            $"astroneer:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AstroneerInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(
                    installRoot,
                    "Astro.exe"),
                [AstroneerInstallationDiscovery.SteamManifestPathKey] = manifest,
                [AstroneerInstallationDiscovery.WorldRootPathKey] = Path.Combine(
                    savedRoot,
                    "SaveGames"),
                [AstroneerInstallationDiscovery.ModsRootPathKey] = Path.Combine(
                    savedRoot,
                    "Mods"),
                [AstroneerInstallationDiscovery.PaksRootPathKey] = Path.Combine(
                    savedRoot,
                    "Paks"),
                [AstroneerInstallationDiscovery.GameSteamAppIdKey] = AstroneerInstallationDiscovery.GameSteamAppId
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
