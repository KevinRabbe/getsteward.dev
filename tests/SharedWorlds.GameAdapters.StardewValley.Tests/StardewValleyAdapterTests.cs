using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.StardewValley.Tests;

public sealed class StardewValleyAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-stardew-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];

    [Fact]
    public void InstallationDiscoveryFindsSteamInstallAndSaveRoot()
    {
        var library = Path.Combine(_root, "steam-library");
        var installRoot = Path.Combine(library, "steamapps", "common", "Stardew Valley");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Stardew Valley.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_413150.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "\"AppState\" { \"buildid\" \"12345\" }");
        var saveRoot = Path.Combine(_root, "AppData", "StardewValley", "Saves");

        var installation = Assert.Single(
            StardewValleyInstallationDiscovery.DiscoverFromSteamLibraries([library], saveRoot));

        Assert.Equal(Path.GetFullPath(installRoot), installation.RootPath);
        Assert.Equal("steam", installation.Source);
        Assert.NotNull(installation.Metadata);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "Stardew Valley.exe")),
            installation.Metadata[StardewValleyInstallationDiscovery.ClientExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(manifest),
            installation.Metadata[StardewValleyInstallationDiscovery.SteamManifestPathKey]);
        Assert.Equal(
            Path.GetFullPath(saveRoot),
            installation.Metadata[StardewValleyInstallationDiscovery.SaveRootPathKey]);
        Assert.Equal("413150", installation.Metadata[StardewValleyInstallationDiscovery.GameSteamAppIdKey]);
    }

    [Fact]
    public void WorldDiscoveryRequiresBothCurrentFiles()
    {
        var saveRoot = Path.Combine(_root, "saves");
        Directory.CreateDirectory(saveRoot);
        CreateSave(saveRoot, "Ready_111", [1, 2, 3], [4, 5, 6]);
        var incomplete = Path.Combine(saveRoot, "Incomplete_222");
        Directory.CreateDirectory(incomplete);
        File.WriteAllBytes(Path.Combine(incomplete, "Incomplete_222"), [7]);

        var world = Assert.Single(StardewValleyWorldDiscovery.DiscoverFromSaveRoot(saveRoot));

        Assert.Equal("local:Ready_111", world.Id);
        Assert.Equal("Ready_111", world.DisplayName);
        Assert.Equal(Path.GetFullPath(Path.Combine(saveRoot, "Ready_111")), world.SourcePath);
    }

    [Fact]
    public void WorldDiscoverySkipsLinkedRequiredFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var saveRoot = Path.Combine(_root, "linked-saves");
        var save = Path.Combine(saveRoot, "Linked_333");
        Directory.CreateDirectory(save);
        File.WriteAllBytes(Path.Combine(save, "Linked_333"), [1, 2, 3]);
        var outside = Path.Combine(_root, "outside-info");
        File.WriteAllBytes(outside, [4, 5, 6]);
        var linked = Path.Combine(save, "SaveGameInfo");
        File.CreateSymbolicLink(linked, outside);
        try
        {
            Assert.Empty(StardewValleyWorldDiscovery.DiscoverFromSaveRoot(saveRoot));
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

        var environment = StardewValleyEnvironment.Inspect(installation);

        Assert.Equal(1, environment.SchemaVersion);
        Assert.Equal("stardew-valley", environment.AdapterId);
        Assert.Equal("24680", environment.GameVersion);
        Assert.Empty(environment.Components);
        Assert.Empty(environment.Configuration);
    }

    [Fact]
    public void EnvironmentInspectionRejectsSmapiInstallation()
    {
        var installation = CreateInstallation("24680");
        File.WriteAllBytes(Path.Combine(installation.RootPath, "StardewModdingAPI.exe"), [1]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StardewValleyEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentInspectionRejectsNonEmptyModsDirectory()
    {
        var installation = CreateInstallation("24680");
        var mods = Path.Combine(installation.RootPath, "Mods", "ExampleMod");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "manifest.json"), "{}");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StardewValleyEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedSteamManifestFailsBeforeParsing()
    {
        var installation = CreateInstallation("12345");
        var manifest = installation.Metadata![StardewValleyInstallationDiscovery.SteamManifestPathKey];
        using (var stream = new FileStream(manifest, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(StardewValleyEnvironment.MaximumSteamManifestBytes + 1);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StardewValleyEnvironment.Inspect(installation));

        Assert.Contains("metadata safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureContainsOnlyCurrentPairAndPreservesSource()
    {
        var saveRoot = Path.Combine(_root, "capture-saves");
        var mainBytes = new byte[] { 1, 2, 3, 4 };
        var infoBytes = new byte[] { 5, 6, 7 };
        var save = CreateSave(saveRoot, "Farm_444", mainBytes, infoBytes);
        File.WriteAllBytes(Path.Combine(save, "Farm_444_old"), [9, 9, 9]);
        File.WriteAllBytes(Path.Combine(save, "SaveGameInfo_old"), [8, 8]);
        File.WriteAllText(Path.Combine(save, "extra.txt"), "not canonical state");
        var adapter = new StardewValleyAdapter();
        var detected = new DetectedWorld("local:Farm_444", "Farm_444", save);

        var captured = await adapter.CaptureDetectedWorldAsync(CreateInstallation("13579"), detected);
        _packages.Add(captured.Package.Path);

        using var archive = ZipFile.OpenRead(captured.Package.Path);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Equal(["Farm_444/Farm_444", "Farm_444/SaveGameInfo"], names.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(mainBytes, File.ReadAllBytes(Path.Combine(save, "Farm_444")));
        Assert.Equal(infoBytes, File.ReadAllBytes(Path.Combine(save, "SaveGameInfo")));
    }

    [Fact]
    public async Task CaptureRestoreCaptureRoundTripPreservesCurrentBytes()
    {
        var installation = CreateInstallation("13579");
        var environment = StardewValleyEnvironment.Inspect(installation);
        var save = CreateSave(
            Path.Combine(_root, "roundtrip-saves"),
            "Farm_555",
            Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray(),
            Enumerable.Range(0, 257).Select(index => (byte)(index % 239)).ToArray());
        var originalMain = File.ReadAllBytes(Path.Combine(save, "Farm_555"));
        var originalInfo = File.ReadAllBytes(Path.Combine(save, "SaveGameInfo"));
        var adapter = new StardewValleyAdapter();
        var detected = new DetectedWorld("local:Farm_555", "Farm_555", save);
        var imported = await adapter.CaptureDetectedWorldAsync(installation, detected);
        _packages.Add(imported.Package.Path);
        var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);
        var workspace = prepared.WorkingDirectory;

        try
        {
            await adapter.RestoreStateAsync(prepared, imported.Package);
            var restored = Path.Combine(workspace, "Saves", "Farm_555");
            Assert.Equal(originalMain, File.ReadAllBytes(Path.Combine(restored, "Farm_555")));
            Assert.Equal(originalInfo, File.ReadAllBytes(Path.Combine(restored, "SaveGameInfo")));

            var recaptured = await adapter.CaptureStateAsync(prepared);
            _packages.Add(recaptured.Package.Path);
            using var archive = ZipFile.OpenRead(recaptured.Package.Path);
            Assert.Equal(originalMain, ReadEntry(archive, "Farm_555/Farm_555"));
            Assert.Equal(originalInfo, ReadEntry(archive, "Farm_555/SaveGameInfo"));

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
    public async Task PrepareEnvironmentRejectsDifferentSteamBuild()
    {
        var installation = CreateInstallation("11111");
        var required = new EnvironmentManifest(
            1,
            "stardew-valley",
            "22222",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var adapter = new StardewValleyAdapter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.PrepareEnvironmentAsync(installation, required));

        Assert.Contains("does not match required build", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterAdvertisesOnlyExactGameVersion()
    {
        var adapter = new StardewValleyAdapter();

        Assert.Equal(GameAdapterCapabilities.ExactGameVersion, adapter.Capabilities);
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var installRoot = Path.Combine(_root, $"install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Stardew Valley.exe"), [1]);
        var manifest = Path.Combine(_root, $"appmanifest-{Guid.NewGuid():N}.acf");
        File.WriteAllText(manifest, $"\"AppState\" {{ \"buildid\" \"{buildId}\" }}");
        return new GameInstallation(
            $"stardew:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [StardewValleyInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(installRoot, "Stardew Valley.exe"),
                [StardewValleyInstallationDiscovery.SteamManifestPathKey] = manifest,
                [StardewValleyInstallationDiscovery.SaveRootPathKey] = Path.Combine(_root, "saves"),
                [StardewValleyInstallationDiscovery.GameSteamAppIdKey] = StardewValleyInstallationDiscovery.GameSteamAppId
            });
    }

    private static string CreateSave(string saveRoot, string name, byte[] mainBytes, byte[] infoBytes)
    {
        var save = Path.Combine(saveRoot, name);
        Directory.CreateDirectory(save);
        File.WriteAllBytes(Path.Combine(save, name), mainBytes);
        File.WriteAllBytes(Path.Combine(save, "SaveGameInfo"), infoBytes);
        return save;
    }

    private static byte[] ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new Xunit.Sdk.XunitException($"Missing archive entry {name}.");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
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
