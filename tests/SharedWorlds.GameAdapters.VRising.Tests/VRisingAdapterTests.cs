using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.VRising.Tests;

public sealed class VRisingAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-v-rising-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];

    [Fact]
    public void InstallationDiscoveryUsesManifestInstallDirectoryAndOwnedV4Root()
    {
        var library = Path.Combine(_root, "steam-library");
        var installRoot = Path.Combine(library, "steamapps", "common", "VRisingCurrent");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "VRising.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_1604030.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            "\"AppState\" { \"appid\" \"1604030\" \"buildid\" \"12345\" \"installdir\" \"VRisingCurrent\" }");
        var saveRoot = Path.Combine(_root, "Saves", "v4");

        var installation = Assert.Single(
            VRisingInstallationDiscovery.DiscoverFromSteamLibraries(
                [library],
                saveRoot));

        Assert.Equal(Path.GetFullPath(installRoot), installation.RootPath);
        Assert.Equal("steam", installation.Source);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "VRising.exe")),
            installation.Metadata![VRisingInstallationDiscovery.ClientExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(manifest),
            installation.Metadata[VRisingInstallationDiscovery.SteamManifestPathKey]);
        Assert.Equal(
            Path.GetFullPath(saveRoot),
            installation.Metadata[VRisingInstallationDiscovery.SaveVersionRootPathKey]);
        Assert.Equal(
            VRisingInstallationDiscovery.GameSteamAppId,
            installation.Metadata[VRisingInstallationDiscovery.GameSteamAppIdKey]);
    }

    [Fact]
    public void InstallationDiscoveryRejectsManifestWithoutInstallDirectory()
    {
        var library = Path.Combine(_root, "bad-manifest-library");
        var manifest = Path.Combine(library, "steamapps", "appmanifest_1604030.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            "\"AppState\" { \"appid\" \"1604030\" \"buildid\" \"12345\" }");

        Assert.Empty(VRisingInstallationDiscovery.DiscoverFromSteamLibraries(
            [library],
            Path.Combine(_root, "Saves", "v4")));
    }

    [Fact]
    public void WorldDiscoveryFindsOnlyCanonicalCompleteV4Session()
    {
        var root = Path.Combine(_root, "discovery");
        Directory.CreateDirectory(root);
        var sessionId = Guid.NewGuid().ToString("D");
        var session = CreateSession(root, sessionId, compressed: true, generations: [2, 7]);
        File.WriteAllText(
            Path.Combine(session, VRisingWorldDiscovery.ServerHostSettingsFileName),
            "{\"Name\":\"Host only\"}");
        CreateSession(root, "not-a-guid", compressed: true, generations: [9]);

        var world = Assert.Single(
            VRisingWorldDiscovery.DiscoverFromSaveVersionRoot(root));
        var bundle = VRisingWorldDiscovery.ResolveCurrentBundle(world.SourcePath);

        Assert.Equal($"local:{sessionId}", world.Id);
        Assert.Equal(sessionId, world.DisplayName);
        Assert.Equal(Path.GetFullPath(session), world.SourcePath);
        Assert.Equal(7UL, bundle.AutoSaveGeneration);
        Assert.Equal("AutoSave_7.save.gz", Path.GetFileName(bundle.AutoSavePath));
    }

    [Fact]
    public void WorldDiscoveryRejectsIncompleteSession()
    {
        var root = Path.Combine(_root, "incomplete");
        var session = CreateSession(
            root,
            Guid.NewGuid().ToString("D"),
            compressed: true,
            generations: [1]);
        File.Delete(Path.Combine(session, VRisingWorldDiscovery.ServerGameSettingsFileName));

        Assert.Empty(VRisingWorldDiscovery.DiscoverFromSaveVersionRoot(root));
    }

    [Fact]
    public void WorldDiscoveryAcceptsUncompressedAutosave()
    {
        var root = Path.Combine(_root, "uncompressed");
        var session = CreateSession(
            root,
            Guid.NewGuid().ToString("D"),
            compressed: false,
            generations: [4]);

        var world = Assert.Single(VRisingWorldDiscovery.DiscoverFromSaveVersionRoot(root));
        var bundle = VRisingWorldDiscovery.ResolveCurrentBundle(world.SourcePath);

        Assert.Equal("AutoSave_4.save", Path.GetFileName(bundle.AutoSavePath));
    }

    [Fact]
    public void WorldDiscoveryRejectsAmbiguousLatestAutosaveRepresentation()
    {
        var root = Path.Combine(_root, "ambiguous");
        var session = CreateSession(
            root,
            Guid.NewGuid().ToString("D"),
            compressed: true,
            generations: [5]);
        File.WriteAllBytes(Path.Combine(session, "AutoSave_5.save"), [9, 8, 7]);

        Assert.Empty(VRisingWorldDiscovery.DiscoverFromSaveVersionRoot(root));
    }

    [Fact]
    public void WorldDiscoveryRejectsUnknownTopLevelPersistence()
    {
        var root = Path.Combine(_root, "unknown");
        var session = CreateSession(
            root,
            Guid.NewGuid().ToString("D"),
            compressed: true,
            generations: [3]);
        File.WriteAllBytes(Path.Combine(session, "FutureState.bin"), [1]);

        Assert.Empty(VRisingWorldDiscovery.DiscoverFromSaveVersionRoot(root));
    }

    [Fact]
    public void WorldDiscoveryRejectsLinkedSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(_root, "linked-session-root");
        Directory.CreateDirectory(root);
        var outsideRoot = Path.Combine(_root, "outside-session-root");
        var sessionId = Guid.NewGuid().ToString("D");
        var outside = CreateSession(
            outsideRoot,
            sessionId,
            compressed: true,
            generations: [1]);
        var linked = Path.Combine(root, sessionId);
        Directory.CreateSymbolicLink(linked, outside);
        try
        {
            Assert.Empty(VRisingWorldDiscovery.DiscoverFromSaveVersionRoot(root));
        }
        finally
        {
            Directory.Delete(linked);
        }
    }

    [Fact]
    public void EnvironmentInspectionUsesExactSteamBuild()
    {
        var installation = CreateInstallation("24680");

        var environment = VRisingEnvironment.Inspect(installation);

        Assert.Equal(1, environment.SchemaVersion);
        Assert.Equal("v-rising", environment.AdapterId);
        Assert.Equal("24680", environment.GameVersion);
        Assert.Empty(environment.Components);
        Assert.Empty(environment.Configuration);
    }

    [Fact]
    public void EnvironmentInspectionRejectsBepInExMarker()
    {
        var installation = CreateInstallation("24680");
        Directory.CreateDirectory(Path.Combine(installation.RootPath, "BepInEx"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            VRisingEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedSteamManifestFailsClosed()
    {
        var installation = CreateInstallation("12345");
        var manifest = installation.Metadata![VRisingInstallationDiscovery.SteamManifestPathKey];
        using (var stream = new FileStream(
                   manifest,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(VRisingSteamManifest.MaximumBytes + 1);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            VRisingEnvironment.Inspect(installation));

        Assert.Contains("metadata safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRestoreCapturePreservesOnlyCurrentWorldOwnedBundle()
    {
        var installation = CreateInstallation("13579");
        var environment = VRisingEnvironment.Inspect(installation);
        var saveRoot = installation.Metadata![VRisingInstallationDiscovery.SaveVersionRootPathKey];
        var session = CreateSession(
            saveRoot,
            Guid.NewGuid().ToString("D"),
            compressed: true,
            generations: [1, 8]);
        var currentBytes = Enumerable.Range(0, 32768)
            .Select(index => (byte)((index * 23) % 251))
            .ToArray();
        File.WriteAllBytes(Path.Combine(session, "AutoSave_8.save.gz"), currentBytes);
        var hostPath = Path.Combine(session, VRisingWorldDiscovery.ServerHostSettingsFileName);
        File.WriteAllText(hostPath, "{\"Name\":\"Machine host config\"}");
        var world = Assert.Single(VRisingWorldDiscovery.DiscoverFromSaveVersionRoot(saveRoot));
        var adapter = new VRisingAdapter();

        var imported = await adapter.CaptureDetectedWorldAsync(installation, world);
        _packages.Add(imported.Package.Path);
        var importedFiles = ReadPackage(imported.Package.Path);
        Assert.Equal(4, importedFiles.Count);
        Assert.Equal(currentBytes, importedFiles["AutoSave_8.save.gz"]);
        Assert.Contains(VRisingWorldDiscovery.ServerGameSettingsFileName, importedFiles.Keys);
        Assert.Contains(VRisingWorldDiscovery.SessionIdFileName, importedFiles.Keys);
        Assert.Contains(VRisingWorldDiscovery.StartDateFileName, importedFiles.Keys);
        Assert.DoesNotContain("AutoSave_1.save.gz", importedFiles.Keys);
        Assert.DoesNotContain(VRisingWorldDiscovery.ServerHostSettingsFileName, importedFiles.Keys);
        Assert.True(File.Exists(hostPath));
        Assert.Equal(currentBytes, File.ReadAllBytes(Path.Combine(session, "AutoSave_8.save.gz")));

        var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);
        var workspace = prepared.WorkingDirectory;
        try
        {
            await adapter.RestoreStateAsync(prepared, imported.Package);
            var recaptured = await adapter.CaptureStateAsync(prepared);
            _packages.Add(recaptured.Package.Path);

            var recapturedFiles = ReadPackage(recaptured.Package.Path);
            Assert.Equal(importedFiles.Keys.Order(), recapturedFiles.Keys.Order());
            foreach (var pair in importedFiles)
            {
                Assert.Equal(pair.Value, recapturedFiles[pair.Key]);
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
    public async Task RestoreRejectsHostInfrastructureInStatePackage()
    {
        var installation = CreateInstallation("13579");
        var environment = VRisingEnvironment.Inspect(installation);
        var package = Path.Combine(_root, "host-config.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "AutoSave_3.save.gz", [1, 2, 3]);
            WriteEntry(archive, VRisingWorldDiscovery.ServerGameSettingsFileName, [4]);
            WriteEntry(archive, VRisingWorldDiscovery.SessionIdFileName, [5]);
            WriteEntry(archive, VRisingWorldDiscovery.StartDateFileName, [6]);
            WriteEntry(archive, VRisingWorldDiscovery.ServerHostSettingsFileName, [7]);
        }

        var adapter = new VRisingAdapter();
        var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                adapter.RestoreStateAsync(
                    prepared,
                    new StatePackage("host-config", package)));
        }
        finally
        {
            await adapter.FinalizePreparedWorldAsync(
                prepared,
                PreparedWorldDisposition.Discard);
        }
    }

    [Fact]
    public async Task RestoreRejectsIncompleteStatePackage()
    {
        var installation = CreateInstallation("13579");
        var environment = VRisingEnvironment.Inspect(installation);
        var package = Path.Combine(_root, "incomplete.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "AutoSave_3.save.gz", [1, 2, 3]);
            WriteEntry(archive, VRisingWorldDiscovery.ServerGameSettingsFileName, [4]);
            WriteEntry(archive, VRisingWorldDiscovery.SessionIdFileName, [5]);
        }

        var adapter = new VRisingAdapter();
        var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                adapter.RestoreStateAsync(
                    prepared,
                    new StatePackage("incomplete", package)));
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
            "v-rising",
            "22222",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var adapter = new VRisingAdapter();

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
            new VRisingAdapter().Capabilities);
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var library = Path.Combine(_root, $"library-{Guid.NewGuid():N}");
        var installName = $"VRising-{Guid.NewGuid():N}";
        var installRoot = Path.Combine(library, "steamapps", "common", installName);
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "VRising.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_1604030.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"appid\" \"1604030\" \"buildid\" \"{buildId}\" \"installdir\" \"{installName}\" }}");
        var saveRoot = Path.Combine(_root, $"saves-{Guid.NewGuid():N}", "v4");
        Directory.CreateDirectory(saveRoot);

        return new GameInstallation(
            $"v-rising:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [VRisingInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(
                    installRoot,
                    "VRising.exe"),
                [VRisingInstallationDiscovery.SteamManifestPathKey] = manifest,
                [VRisingInstallationDiscovery.SaveVersionRootPathKey] = saveRoot,
                [VRisingInstallationDiscovery.GameSteamAppIdKey] =
                    VRisingInstallationDiscovery.GameSteamAppId
            });
    }

    private static string CreateSession(
        string saveRoot,
        string sessionName,
        bool compressed,
        IReadOnlyList<int> generations)
    {
        Directory.CreateDirectory(saveRoot);
        var session = Path.Combine(saveRoot, sessionName);
        Directory.CreateDirectory(session);
        foreach (var generation in generations)
        {
            var extension = compressed ? ".save.gz" : ".save";
            File.WriteAllBytes(
                Path.Combine(session, $"AutoSave_{generation}{extension}"),
                [(byte)(generation + 1), (byte)(generation + 2)]);
        }

        File.WriteAllText(
            Path.Combine(session, VRisingWorldDiscovery.ServerGameSettingsFileName),
            "{\"GameModeType\":\"PvE\"}");
        File.WriteAllText(
            Path.Combine(session, VRisingWorldDiscovery.SessionIdFileName),
            $"\"{sessionName}\"");
        File.WriteAllText(
            Path.Combine(session, VRisingWorldDiscovery.StartDateFileName),
            "\"2026-07-26T00:00:00Z\"");
        return session;
    }

    private static Dictionary<string, byte[]> ReadPackage(string packagePath)
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

    private static void WriteEntry(
        ZipArchive archive,
        string name,
        byte[] bytes)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(bytes);
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
