using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.Enshrouded.Tests;

public sealed class EnshroudedAdapterTests : IDisposable
{
    private const string WorldId = "34d85178";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-enshrouded-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];

    [Fact]
    public void InstallationDiscoveryFindsSteamInstallAndLocalSaveRoot()
    {
        var library = Path.Combine(_root, "steam-library");
        var installRoot = Path.Combine(library, "steamapps", "common", "Enshrouded");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "enshrouded.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_1203620.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "\"AppState\" { \"buildid\" \"12345\" }");
        var saveRoot = Path.Combine(_root, "Saved Games", "enshrouded");

        var installation = Assert.Single(
            EnshroudedInstallationDiscovery.DiscoverFromSteamLibraries(
                [library],
                saveRoot));

        Assert.Equal(Path.GetFullPath(installRoot), installation.RootPath);
        Assert.Equal("steam", installation.Source);
        Assert.NotNull(installation.Metadata);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "enshrouded.exe")),
            installation.Metadata[EnshroudedInstallationDiscovery.ClientExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(manifest),
            installation.Metadata[EnshroudedInstallationDiscovery.SteamManifestPathKey]);
        Assert.Equal(
            Path.GetFullPath(saveRoot),
            installation.Metadata[EnshroudedInstallationDiscovery.SaveGamesRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "mods")),
            installation.Metadata[EnshroudedInstallationDiscovery.ModsRootPathKey]);
        Assert.Equal(
            "1203620",
            installation.Metadata[EnshroudedInstallationDiscovery.GameSteamAppIdKey]);
    }

    [Fact]
    public void WorldDiscoveryUsesIndexesAndExcludesRecoveryAndUserConfig()
    {
        var saveRoot = Path.Combine(_root, "save-games");
        CreateWorldBundle(saveRoot, WorldId, 3, 5, [1, 2, 3], [4, 5]);
        File.WriteAllBytes(Path.Combine(saveRoot, $"{WorldId}-2"), [9]);
        File.WriteAllBytes(Path.Combine(saveRoot, $"{WorldId}_info-4"), [8]);
        File.WriteAllText(Path.Combine(saveRoot, "enshrouded_user.json"), "{}");
        File.WriteAllBytes(Path.Combine(saveRoot, "deadbeef"), [7]);
        File.WriteAllText(
            Path.Combine(saveRoot, "deadbeef-index"),
            "{\"latest\":0,\"deleted\":false}");

        var world = Assert.Single(
            EnshroudedWorldDiscovery.DiscoverFromSaveGamesRoot(saveRoot));

        Assert.Equal($"local:{WorldId}", world.Id);
        Assert.Equal("World 7", world.DisplayName);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(saveRoot, $"{WorldId}-index")),
            world.SourcePath);
        var bundle = EnshroudedWorldDiscovery.ResolveFromDataIndexPath(world.SourcePath);
        Assert.Equal(3, bundle.DataLatest);
        Assert.Equal(5, bundle.InfoLatest);
        Assert.EndsWith($"{WorldId}-3", bundle.DataPath, StringComparison.Ordinal);
        Assert.EndsWith($"{WorldId}_info-5", bundle.InfoPath, StringComparison.Ordinal);
    }

    [Fact]
    public void WorldDiscoverySkipsDeletedWorld()
    {
        var saveRoot = Path.Combine(_root, "deleted-world");
        CreateWorldBundle(saveRoot, WorldId, 0, 0, [1], [2]);
        WriteIndex(Path.Combine(saveRoot, $"{WorldId}-index"), 0, deleted: true);

        Assert.Empty(EnshroudedWorldDiscovery.DiscoverFromSaveGamesRoot(saveRoot));
    }

    [Fact]
    public void WorldDiscoverySkipsOversizedIndex()
    {
        var saveRoot = Path.Combine(_root, "oversized-index");
        CreateWorldBundle(saveRoot, WorldId, 0, 0, [1], [2]);
        using (var stream = new FileStream(
                   Path.Combine(saveRoot, $"{WorldId}-index"),
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(EnshroudedWorldDiscovery.MaximumIndexBytes + 1);
        }

        Assert.Empty(EnshroudedWorldDiscovery.DiscoverFromSaveGamesRoot(saveRoot));
    }

    [Fact]
    public void WorldDiscoverySkipsLinkedActiveWorldFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var saveRoot = Path.Combine(_root, "linked-world");
        Directory.CreateDirectory(saveRoot);
        WriteIndex(Path.Combine(saveRoot, $"{WorldId}-index"), 2);
        WriteIndex(Path.Combine(saveRoot, $"{WorldId}_info-index"), 0);
        var outside = Path.Combine(_root, "outside-world");
        File.WriteAllBytes(outside, [1, 2, 3]);
        var linked = Path.Combine(saveRoot, $"{WorldId}-2");
        File.CreateSymbolicLink(linked, outside);
        File.WriteAllBytes(Path.Combine(saveRoot, $"{WorldId}_info"), [4]);
        try
        {
            Assert.Empty(EnshroudedWorldDiscovery.DiscoverFromSaveGamesRoot(saveRoot));
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

        var environment = EnshroudedEnvironment.Inspect(installation);

        Assert.Equal(1, environment.SchemaVersion);
        Assert.Equal("enshrouded", environment.AdapterId);
        Assert.Equal("24680", environment.GameVersion);
        Assert.Empty(environment.Components);
        Assert.Empty(environment.Configuration);
    }

    [Fact]
    public void EnvironmentInspectionRejectsModLoaderMarker()
    {
        var installation = CreateInstallation("24680");
        File.WriteAllBytes(Path.Combine(installation.RootPath, "dbghelp.dll"), [1]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            EnshroudedEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentInspectionRejectsNonEmptyModsDirectory()
    {
        var installation = CreateInstallation("24680");
        var modsRoot = installation.Metadata![EnshroudedInstallationDiscovery.ModsRootPathKey];
        Directory.CreateDirectory(modsRoot);
        File.WriteAllBytes(Path.Combine(modsRoot, "example.dll"), [1]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            EnshroudedEnvironment.Inspect(installation));

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
        var modsRoot = installation.Metadata![EnshroudedInstallationDiscovery.ModsRootPathKey];
        var outside = Path.Combine(_root, "outside-mods");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(modsRoot, outside);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                EnshroudedEnvironment.Inspect(installation));

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
        var manifest = installation.Metadata![EnshroudedInstallationDiscovery.SteamManifestPathKey];
        using (var stream = new FileStream(
                   manifest,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(EnshroudedEnvironment.MaximumSteamManifestBytes + 1);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            EnshroudedEnvironment.Inspect(installation));

        Assert.Contains("metadata safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRestoreCapturePreservesOnlyCurrentIndexedBytes()
    {
        var installation = CreateInstallation("13579");
        var environment = EnshroudedEnvironment.Inspect(installation);
        var saveRoot = installation.Metadata![EnshroudedInstallationDiscovery.SaveGamesRootPathKey];
        var dataBytes = Enumerable.Range(0, 8192)
            .Select(index => (byte)(index % 251))
            .ToArray();
        var infoBytes = Enumerable.Range(0, 1024)
            .Select(index => (byte)((index * 3) % 251))
            .ToArray();
        CreateWorldBundle(saveRoot, WorldId, 3, 5, dataBytes, infoBytes);
        File.WriteAllBytes(Path.Combine(saveRoot, $"{WorldId}-2"), [9, 9]);
        File.WriteAllBytes(Path.Combine(saveRoot, $"{WorldId}_info-4"), [8, 8]);
        File.WriteAllText(Path.Combine(saveRoot, "enshrouded_user.json"), "{\"keybind\":true}");
        var detected = Assert.Single(
            EnshroudedWorldDiscovery.DiscoverFromSaveGamesRoot(saveRoot));
        var adapter = new EnshroudedAdapter();

        var imported = await adapter.CaptureDetectedWorldAsync(
            installation,
            detected);
        _packages.Add(imported.Package.Path);
        var importedFiles = ReadArchiveFiles(imported.Package.Path);
        Assert.Equal(
            new[]
            {
                WorldId + "-3",
                WorldId + "-index",
                WorldId + "_info-5",
                WorldId + "_info-index"
            },
            importedFiles.Keys.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(dataBytes, importedFiles[$"{WorldId}-3"]);
        Assert.Equal(infoBytes, importedFiles[$"{WorldId}_info-5"]);
        Assert.Equal(dataBytes, File.ReadAllBytes(Path.Combine(saveRoot, $"{WorldId}-3")));
        Assert.True(File.Exists(Path.Combine(saveRoot, $"{WorldId}-2")));
        Assert.True(File.Exists(Path.Combine(saveRoot, "enshrouded_user.json")));

        var prepared = await adapter.PrepareEnvironmentAsync(
            installation,
            environment);
        var workspace = prepared.WorkingDirectory;
        try
        {
            await adapter.RestoreStateAsync(prepared, imported.Package);
            var restoredRoot = Path.Combine(workspace, "savegame");
            Assert.Equal(
                importedFiles.Keys.OrderBy(name => name, StringComparer.Ordinal),
                Directory.EnumerateFiles(restoredRoot)
                    .Select(Path.GetFileName)
                    .OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal(dataBytes, File.ReadAllBytes(Path.Combine(restoredRoot, $"{WorldId}-3")));
            Assert.Equal(infoBytes, File.ReadAllBytes(Path.Combine(restoredRoot, $"{WorldId}_info-5")));

            var recaptured = await adapter.CaptureStateAsync(prepared);
            _packages.Add(recaptured.Package.Path);
            var recapturedFiles = ReadArchiveFiles(recaptured.Package.Path);
            Assert.Equal(importedFiles.Keys.OrderBy(name => name), recapturedFiles.Keys.OrderBy(name => name));
            foreach (var (name, bytes) in importedFiles)
            {
                Assert.Equal(bytes, recapturedFiles[name]);
            }

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
    public async Task RestoreRejectsPackageWhoseIndexSelectsMissingRevision()
    {
        var installation = CreateInstallation("13579");
        var environment = EnshroudedEnvironment.Inspect(installation);
        var packagePath = Path.Combine(_root, "mismatched.zip");
        CreatePackage(
            packagePath,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [$"{WorldId}-index"] = IndexBytes(3),
                [$"{WorldId}-2"] = [1],
                [$"{WorldId}_info-index"] = IndexBytes(0),
                [$"{WorldId}_info"] = [2]
            });
        var adapter = new EnshroudedAdapter();
        var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                adapter.RestoreStateAsync(
                    prepared,
                    new StatePackage("mismatched", packagePath)));
            Assert.Contains("active files", exception.Message, StringComparison.Ordinal);
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
            "enshrouded",
            "22222",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var adapter = new EnshroudedAdapter();

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
        var adapter = new EnshroudedAdapter();

        Assert.Equal(GameAdapterCapabilities.ExactGameVersion, adapter.Capabilities);
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var library = Path.Combine(_root, $"library-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(library, "steamapps", "common", "Enshrouded");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "enshrouded.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_1203620.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"buildid\" \"{buildId}\" }}");
        var saveRoot = Path.Combine(_root, $"save-games-{Guid.NewGuid():N}");

        return new GameInstallation(
            $"enshrouded:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [EnshroudedInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(
                    installRoot,
                    "enshrouded.exe"),
                [EnshroudedInstallationDiscovery.SteamManifestPathKey] = manifest,
                [EnshroudedInstallationDiscovery.SaveGamesRootPathKey] = saveRoot,
                [EnshroudedInstallationDiscovery.ModsRootPathKey] = Path.Combine(
                    installRoot,
                    "mods"),
                [EnshroudedInstallationDiscovery.GameSteamAppIdKey] = EnshroudedInstallationDiscovery.GameSteamAppId
            });
    }

    private static void CreateWorldBundle(
        string saveRoot,
        string worldId,
        int dataLatest,
        int infoLatest,
        byte[] dataBytes,
        byte[] infoBytes)
    {
        Directory.CreateDirectory(saveRoot);
        WriteIndex(Path.Combine(saveRoot, $"{worldId}-index"), dataLatest);
        WriteIndex(Path.Combine(saveRoot, $"{worldId}_info-index"), infoLatest);
        File.WriteAllBytes(
            Path.Combine(saveRoot, EnshroudedWorldDiscovery.GetDataFileName(worldId, dataLatest)),
            dataBytes);
        File.WriteAllBytes(
            Path.Combine(saveRoot, EnshroudedWorldDiscovery.GetInfoFileName(worldId, infoLatest)),
            infoBytes);
    }

    private static void WriteIndex(
        string path,
        int latest,
        bool deleted = false)
        => File.WriteAllBytes(path, IndexBytes(latest, deleted));

    private static byte[] IndexBytes(int latest, bool deleted = false)
        => System.Text.Encoding.UTF8.GetBytes(
            $"{{\"latest\":{latest},\"time\":1752653773,\"deleted\":{deleted.ToString().ToLowerInvariant()}}}");

    private static Dictionary<string, byte[]> ReadArchiveFiles(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using var stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            },
            StringComparer.Ordinal);
    }

    private static void CreatePackage(
        string path,
        IReadOnlyDictionary<string, byte[]> files)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in files)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(bytes);
        }
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
