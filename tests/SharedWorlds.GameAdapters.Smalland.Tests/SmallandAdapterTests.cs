using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.Smalland.Tests;

public sealed class SmallandAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-smalland-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];

    [Fact]
    public void InstallationDiscoveryFindsSteamInstallAndOwnedRoots()
    {
        var library = Path.Combine(_root, "steam-library");
        var installRoot = Path.Combine(library, "steamapps", "common", "SMALLAND");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "SMALLAND.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_768200.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            "\"AppState\" { \"appid\" \"768200\" \"buildid\" \"12345\" }");
        var saveRoot = Path.Combine(_root, "SaveGames");

        var installation = Assert.Single(
            SmallandInstallationDiscovery.DiscoverFromSteamLibraries(
                [library],
                saveRoot));

        Assert.Equal(Path.GetFullPath(installRoot), installation.RootPath);
        Assert.Equal("steam", installation.Source);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "SMALLAND.exe")),
            installation.Metadata![SmallandInstallationDiscovery.ClientExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(saveRoot),
            installation.Metadata[SmallandInstallationDiscovery.SaveGamesRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "SMALLAND", "Content", "Paks")),
            installation.Metadata[SmallandInstallationDiscovery.PaksRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(manifest),
            installation.Metadata[SmallandInstallationDiscovery.SteamManifestPathKey]);
    }

    [Fact]
    public void WorldDiscoveryFindsOnlyDirectWorldFiles()
    {
        var saveRoot = Path.Combine(_root, "discovery");
        var worlds = Path.Combine(saveRoot, "Worlds");
        var players = Path.Combine(saveRoot, "Players");
        Directory.CreateDirectory(worlds);
        Directory.CreateDirectory(players);
        File.WriteAllBytes(Path.Combine(worlds, "MyWorld.wld"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(players, "PlayerOne.plr"), [4]);
        File.WriteAllBytes(Path.Combine(saveRoot, "PlayerOne_MA.sav"), [5]);
        var nested = Path.Combine(worlds, "backup");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "OldWorld.wld"), [6]);
        File.WriteAllBytes(Path.Combine(worlds, "not-a-world.sav"), [7]);

        var world = Assert.Single(
            SmallandWorldDiscovery.DiscoverFromSaveGamesRoot(saveRoot));

        Assert.Equal("local:MyWorld", world.Id);
        Assert.Equal("MyWorld", world.DisplayName);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(worlds, "MyWorld.wld")),
            world.SourcePath);
    }

    [Fact]
    public void MissingWorldsDirectoryMeansNoWorlds()
    {
        var saveRoot = Path.Combine(_root, "missing-worlds");
        Directory.CreateDirectory(saveRoot);
        File.WriteAllBytes(Path.Combine(saveRoot, "Map_MA.sav"), [1]);

        Assert.Empty(SmallandWorldDiscovery.DiscoverFromSaveGamesRoot(saveRoot));
    }

    [Fact]
    public void WorldDiscoveryRejectsLinkedWorld()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var saveRoot = Path.Combine(_root, "linked-world");
        var worlds = Path.Combine(saveRoot, "Worlds");
        Directory.CreateDirectory(worlds);
        var outside = Path.Combine(_root, "outside.wld");
        File.WriteAllBytes(outside, [1, 2, 3]);
        var linked = Path.Combine(worlds, "Linked.wld");
        File.CreateSymbolicLink(linked, outside);
        try
        {
            Assert.Empty(SmallandWorldDiscovery.DiscoverFromSaveGamesRoot(saveRoot));
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

        var environment = SmallandEnvironment.Inspect(installation);

        Assert.Equal(1, environment.SchemaVersion);
        Assert.Equal("smalland", environment.AdapterId);
        Assert.Equal("24680", environment.GameVersion);
        Assert.Empty(environment.Components);
        Assert.Empty(environment.Configuration);
    }

    [Fact]
    public void EnvironmentInspectionAcceptsStockNamedPak()
    {
        var installation = CreateInstallation("24680");
        var paks = installation.Metadata![SmallandInstallationDiscovery.PaksRootPathKey];
        Directory.CreateDirectory(paks);
        File.WriteAllBytes(
            Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak"),
            [1]);

        var environment = SmallandEnvironment.Inspect(installation);

        Assert.Equal("24680", environment.GameVersion);
    }

    [Fact]
    public void EnvironmentInspectionRejectsExtraPakMod()
    {
        var installation = CreateInstallation("24680");
        var paks = installation.Metadata![SmallandInstallationDiscovery.PaksRootPathKey];
        Directory.CreateDirectory(paks);
        File.WriteAllBytes(
            Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak"),
            [1]);
        File.WriteAllBytes(Path.Combine(paks, "BiggerChests.pak"), [2]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SmallandEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentInspectionRejectsLinkedPaksDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var installation = CreateInstallation("24680");
        var paks = installation.Metadata![SmallandInstallationDiscovery.PaksRootPathKey];
        var outside = Path.Combine(_root, "outside-paks");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.GetDirectoryName(paks)!);
        Directory.CreateSymbolicLink(paks, outside);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                SmallandEnvironment.Inspect(installation));
            Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(paks);
        }
    }

    [Fact]
    public void EnvironmentInspectionRejectsLinkedStockNamedPak()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var installation = CreateInstallation("24680");
        var paks = installation.Metadata![SmallandInstallationDiscovery.PaksRootPathKey];
        Directory.CreateDirectory(paks);
        var outside = Path.Combine(_root, "outside-stock.pak");
        File.WriteAllBytes(outside, [1]);
        var linked = Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak");
        File.CreateSymbolicLink(linked, outside);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                SmallandEnvironment.Inspect(installation));
            Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(linked);
        }
    }

    [Fact]
    public void OversizedSteamManifestFailsClosed()
    {
        var installation = CreateInstallation("12345");
        var manifest = installation.Metadata![SmallandInstallationDiscovery.SteamManifestPathKey];
        using (var stream = new FileStream(
                   manifest,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(SmallandEnvironment.MaximumSteamManifestBytes + 1);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SmallandEnvironment.Inspect(installation));

        Assert.Contains("metadata safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRestoreCapturePreservesOnlyWorldBytes()
    {
        var installation = CreateInstallation("13579");
        var environment = SmallandEnvironment.Inspect(installation);
        var saveRoot = installation.Metadata![SmallandInstallationDiscovery.SaveGamesRootPathKey];
        var worlds = Path.Combine(saveRoot, "Worlds");
        var players = Path.Combine(saveRoot, "Players");
        Directory.CreateDirectory(worlds);
        Directory.CreateDirectory(players);
        var source = Path.Combine(worlds, "MyWorld.wld");
        var bytes = Enumerable.Range(0, 16384)
            .Select(index => (byte)((index * 19) % 251))
            .ToArray();
        File.WriteAllBytes(source, bytes);
        File.WriteAllBytes(Path.Combine(players, "PlayerOne.plr"), [8, 8, 8]);
        File.WriteAllBytes(Path.Combine(saveRoot, "PlayerOne_MA.sav"), [7, 7, 7]);
        var detected = Assert.Single(
            SmallandWorldDiscovery.DiscoverFromSaveGamesRoot(saveRoot));
        var adapter = new SmallandAdapter();

        var imported = await adapter.CaptureDetectedWorldAsync(
            installation,
            detected);
        _packages.Add(imported.Package.Path);
        Assert.Equal(bytes, File.ReadAllBytes(imported.Package.Path));
        Assert.Equal(bytes, File.ReadAllBytes(source));

        var prepared = await adapter.PrepareEnvironmentAsync(
            installation,
            environment);
        var workspace = prepared.WorkingDirectory;
        try
        {
            await adapter.RestoreStateAsync(prepared, imported.Package);
            Assert.Equal(
                bytes,
                File.ReadAllBytes(Path.Combine(workspace, "save", "world.wld")));

            var recaptured = await adapter.CaptureStateAsync(prepared);
            _packages.Add(recaptured.Package.Path);
            Assert.Equal(bytes, File.ReadAllBytes(recaptured.Package.Path));

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
    public async Task RestoreRejectsEmptyWorldPackage()
    {
        var installation = CreateInstallation("13579");
        var environment = SmallandEnvironment.Inspect(installation);
        var empty = Path.Combine(_root, "empty.wld");
        File.WriteAllBytes(empty, []);
        var adapter = new SmallandAdapter();
        var prepared = await adapter.PrepareEnvironmentAsync(
            installation,
            environment);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.RestoreStateAsync(
                    prepared,
                    new StatePackage("empty", empty)));
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
            "smalland",
            "22222",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var adapter = new SmallandAdapter();

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
        Assert.Equal(
            GameAdapterCapabilities.ExactGameVersion,
            new SmallandAdapter().Capabilities);
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var library = Path.Combine(_root, $"library-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(library, "steamapps", "common", "SMALLAND");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "SMALLAND.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_768200.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"appid\" \"768200\" \"buildid\" \"{buildId}\" }}");
        var saveRoot = Path.Combine(_root, $"save-games-{Guid.NewGuid():N}");

        return new GameInstallation(
            $"smalland:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SmallandInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(
                    installRoot,
                    "SMALLAND.exe"),
                [SmallandInstallationDiscovery.SteamManifestPathKey] = manifest,
                [SmallandInstallationDiscovery.SaveGamesRootPathKey] = saveRoot,
                [SmallandInstallationDiscovery.PaksRootPathKey] = Path.Combine(
                    installRoot,
                    "SMALLAND",
                    "Content",
                    "Paks"),
                [SmallandInstallationDiscovery.GameSteamAppIdKey] =
                    SmallandInstallationDiscovery.GameSteamAppId
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
