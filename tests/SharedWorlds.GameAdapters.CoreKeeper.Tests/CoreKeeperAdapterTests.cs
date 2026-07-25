using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.CoreKeeper.Tests;

public sealed class CoreKeeperAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-core-keeper-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];

    [Fact]
    public void InstallationDiscoveryFindsSteamInstallAndOwnedRoots()
    {
        var library = Path.Combine(_root, "steam-library");
        var installRoot = Path.Combine(library, "steamapps", "common", "Core Keeper");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "CoreKeeper.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_1621690.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "\"AppState\" { \"buildid\" \"12345\" }");
        var profilesRoot = Path.Combine(_root, "LocalLow", "Pugstorm", "Core Keeper", "Steam");

        var installation = Assert.Single(
            CoreKeeperInstallationDiscovery.DiscoverFromSteamLibraries([library], profilesRoot));

        Assert.Equal(Path.GetFullPath(installRoot), installation.RootPath);
        Assert.Equal("steam", installation.Source);
        Assert.NotNull(installation.Metadata);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "CoreKeeper.exe")),
            installation.Metadata[CoreKeeperInstallationDiscovery.ClientExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(manifest),
            installation.Metadata[CoreKeeperInstallationDiscovery.SteamManifestPathKey]);
        Assert.Equal(
            Path.GetFullPath(profilesRoot),
            installation.Metadata[CoreKeeperInstallationDiscovery.SaveProfilesRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                library,
                "steamapps",
                "workshop",
                "content",
                "1621690")),
            installation.Metadata[CoreKeeperInstallationDiscovery.WorkshopContentRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                installRoot,
                "CoreKeeper_Data",
                "StreamingAssets",
                "Mods")),
            installation.Metadata[CoreKeeperInstallationDiscovery.ManualModsRootPathKey]);
        Assert.Equal(
            "1621690",
            installation.Metadata[CoreKeeperInstallationDiscovery.GameSteamAppIdKey]);
    }

    [Fact]
    public void WorldDiscoveryRequiresAllThreeCurrentSlotFiles()
    {
        var profilesRoot = Path.Combine(_root, "profiles");
        var profile = Path.Combine(profilesRoot, "76561198000000000");
        _ = CreateWorldBundle(profile, "0", [1, 2, 3], [4, 5], [6, 7]);

        File.WriteAllBytes(Path.Combine(profile, "worlds", "1.world.gzip"), [8]);
        File.WriteAllBytes(Path.Combine(profile, "worldinfos", "1.worldinfo"), [9]);

        var world = Assert.Single(
            CoreKeeperWorldDiscovery.DiscoverFromProfilesRoot(profilesRoot));

        Assert.Equal("local:76561198000000000:0", world.Id);
        Assert.Equal("Core Keeper World 1", world.DisplayName);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(profile, "worlds", "0.world.gzip")),
            world.SourcePath);
    }

    [Fact]
    public void WorldDiscoverySkipsRecoveryCopiesAndNonCanonicalSlots()
    {
        var profilesRoot = Path.Combine(_root, "profiles-recovery");
        var profile = Path.Combine(profilesRoot, "profile");
        var main = CreateWorldBundle(profile, "0", [1], [2], [3]);
        File.WriteAllBytes(main + ".pugbackup", [9]);
        File.WriteAllBytes(Path.Combine(profile, "worlds", "01.world.gzip"), [8]);
        File.WriteAllBytes(Path.Combine(profile, "worldinfos", "01.worldinfo"), [8]);
        File.WriteAllBytes(Path.Combine(profile, "worldgenparams", "01.json"), [8]);

        var world = Assert.Single(
            CoreKeeperWorldDiscovery.DiscoverFromProfilesRoot(profilesRoot));

        Assert.Equal("local:profile:0", world.Id);
    }

    [Fact]
    public void WorldDiscoverySkipsLinkedRequiredFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var profilesRoot = Path.Combine(_root, "linked-profiles");
        var profile = Path.Combine(profilesRoot, "profile");
        Directory.CreateDirectory(Path.Combine(profile, "worlds"));
        Directory.CreateDirectory(Path.Combine(profile, "worldinfos"));
        Directory.CreateDirectory(Path.Combine(profile, "worldgenparams"));
        File.WriteAllBytes(Path.Combine(profile, "worlds", "0.world.gzip"), [1]);
        File.WriteAllBytes(Path.Combine(profile, "worldinfos", "0.worldinfo"), [2]);
        var outside = Path.Combine(_root, "outside.json");
        File.WriteAllBytes(outside, [3]);
        var linked = Path.Combine(profile, "worldgenparams", "0.json");
        File.CreateSymbolicLink(linked, outside);
        try
        {
            Assert.Empty(CoreKeeperWorldDiscovery.DiscoverFromProfilesRoot(profilesRoot));
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

        var environment = CoreKeeperEnvironment.Inspect(installation);

        Assert.Equal(1, environment.SchemaVersion);
        Assert.Equal("core-keeper", environment.AdapterId);
        Assert.Equal("24680", environment.GameVersion);
        Assert.Empty(environment.Components);
        Assert.Empty(environment.Configuration);
    }

    [Fact]
    public void EnvironmentInspectionRejectsManualMods()
    {
        var installation = CreateInstallation("24680");
        var modsRoot = installation.Metadata![CoreKeeperInstallationDiscovery.ManualModsRootPathKey];
        Directory.CreateDirectory(modsRoot);
        File.WriteAllBytes(Path.Combine(modsRoot, "example.dll"), [1]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CoreKeeperEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentInspectionRejectsWorkshopContent()
    {
        var installation = CreateInstallation("24680");
        var workshopRoot = installation.Metadata![CoreKeeperInstallationDiscovery.WorkshopContentRootPathKey];
        var modRoot = Path.Combine(workshopRoot, "1234567890");
        Directory.CreateDirectory(modRoot);
        File.WriteAllText(Path.Combine(modRoot, "mod.json"), "{}");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CoreKeeperEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentInspectionRejectsUserProfileMods()
    {
        var installation = CreateInstallation("24680");
        var profilesRoot = installation.Metadata![CoreKeeperInstallationDiscovery.SaveProfilesRootPathKey];
        var modsRoot = Path.Combine(profilesRoot, "profile", "mods");
        Directory.CreateDirectory(modsRoot);
        File.WriteAllBytes(Path.Combine(modsRoot, "example.mod"), [1]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CoreKeeperEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedSteamManifestFailsBeforeParsing()
    {
        var installation = CreateInstallation("12345");
        var manifest = installation.Metadata![CoreKeeperInstallationDiscovery.SteamManifestPathKey];
        using (var stream = new FileStream(
                   manifest,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(CoreKeeperEnvironment.MaximumSteamManifestBytes + 1);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CoreKeeperEnvironment.Inspect(installation));

        Assert.Contains("metadata safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureContainsOnlyWorldOwnedCurrentFilesAndPreservesSource()
    {
        var profile = Path.Combine(_root, "capture-profile");
        byte[] worldBytes = [1, 2, 3, 4];
        byte[] infoBytes = [5, 6];
        byte[] generationBytes = [7, 8, 9];
        var worldPath = CreateWorldBundle(
            profile,
            "2",
            worldBytes,
            infoBytes,
            generationBytes);
        File.WriteAllBytes(worldPath + ".pugbackup", [99]);
        Directory.CreateDirectory(Path.Combine(profile, "saves"));
        File.WriteAllText(Path.Combine(profile, "saves", "0.json"), "player-owned");
        Directory.CreateDirectory(Path.Combine(profile, "maps", "0"));
        File.WriteAllBytes(
            Path.Combine(profile, "maps", "0", "2.mapparts.gzip"),
            [88]);
        var adapter = new CoreKeeperAdapter();
        var detected = new DetectedWorld(
            "local:profile:2",
            "Core Keeper World 3",
            worldPath);

        var captured = await adapter.CaptureDetectedWorldAsync(
            CreateInstallation("13579"),
            detected);
        _packages.Add(captured.Package.Path);

        using var archive = ZipFile.OpenRead(captured.Package.Path);
        var names = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .Select(entry => entry.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "worldgenparams/2.json",
                "worldinfos/2.worldinfo",
                "worlds/2.world.gzip"
            },
            names);
        Assert.Equal(worldBytes, File.ReadAllBytes(worldPath));
        Assert.Equal(
            infoBytes,
            File.ReadAllBytes(Path.Combine(profile, "worldinfos", "2.worldinfo")));
        Assert.Equal(
            generationBytes,
            File.ReadAllBytes(Path.Combine(profile, "worldgenparams", "2.json")));
    }

    [Fact]
    public async Task CaptureRestoreCaptureRoundTripPreservesAllThreeWorldFiles()
    {
        var installation = CreateInstallation("13579");
        var environment = CoreKeeperEnvironment.Inspect(installation);
        var profile = Path.Combine(_root, "roundtrip-profile");
        var worldBytes = Enumerable.Range(0, 4096)
            .Select(index => (byte)(index % 251))
            .ToArray();
        var infoBytes = Enumerable.Range(0, 257)
            .Select(index => (byte)(index % 239))
            .ToArray();
        var generationBytes = Enumerable.Range(0, 513)
            .Select(index => (byte)(index % 233))
            .ToArray();
        var worldPath = CreateWorldBundle(
            profile,
            "0",
            worldBytes,
            infoBytes,
            generationBytes);
        var adapter = new CoreKeeperAdapter();
        var detected = new DetectedWorld(
            "local:profile:0",
            "Core Keeper World 1",
            worldPath);
        var imported = await adapter.CaptureDetectedWorldAsync(installation, detected);
        _packages.Add(imported.Package.Path);
        var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);
        var workspace = prepared.WorkingDirectory;

        try
        {
            await adapter.RestoreStateAsync(prepared, imported.Package);
            var restoredProfile = Path.Combine(workspace, "profile");
            Assert.Equal(
                worldBytes,
                File.ReadAllBytes(Path.Combine(
                    restoredProfile,
                    "worlds",
                    "0.world.gzip")));
            Assert.Equal(
                infoBytes,
                File.ReadAllBytes(Path.Combine(
                    restoredProfile,
                    "worldinfos",
                    "0.worldinfo")));
            Assert.Equal(
                generationBytes,
                File.ReadAllBytes(Path.Combine(
                    restoredProfile,
                    "worldgenparams",
                    "0.json")));

            var recaptured = await adapter.CaptureStateAsync(prepared);
            _packages.Add(recaptured.Package.Path);
            using var archive = ZipFile.OpenRead(recaptured.Package.Path);
            Assert.Equal(worldBytes, ReadEntry(archive, "worlds/0.world.gzip"));
            Assert.Equal(infoBytes, ReadEntry(archive, "worldinfos/0.worldinfo"));
            Assert.Equal(
                generationBytes,
                ReadEntry(archive, "worldgenparams/0.json"));

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
    public async Task RestoreRejectsPackageWhoseFilesUseDifferentSlots()
    {
        var installation = CreateInstallation("13579");
        var environment = CoreKeeperEnvironment.Inspect(installation);
        var badPackage = Path.Combine(_root, "bad-slots.zip");
        using (var archive = ZipFile.Open(badPackage, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "worlds/0.world.gzip", [1]);
            WriteEntry(archive, "worldinfos/1.worldinfo", [2]);
            WriteEntry(archive, "worldgenparams/0.json", [3]);
        }

        _packages.Add(badPackage);
        var adapter = new CoreKeeperAdapter();
        var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);
        var workspace = prepared.WorkingDirectory;

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                adapter.RestoreStateAsync(
                    prepared,
                    new StatePackage("bad", badPackage)));

            Assert.Contains(
                "more than one World slot",
                exception.Message,
                StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(workspace, "profile")));
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
            "core-keeper",
            "22222",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var adapter = new CoreKeeperAdapter();

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
        var adapter = new CoreKeeperAdapter();

        Assert.Equal(GameAdapterCapabilities.ExactGameVersion, adapter.Capabilities);
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var library = Path.Combine(_root, $"library-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(library, "steamapps", "common", "Core Keeper");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "CoreKeeper.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_1621690.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"buildid\" \"{buildId}\" }}");
        var profilesRoot = Path.Combine(_root, $"profiles-{Guid.NewGuid():N}");

        return new GameInstallation(
            $"core-keeper:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [CoreKeeperInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(
                    installRoot,
                    "CoreKeeper.exe"),
                [CoreKeeperInstallationDiscovery.SteamManifestPathKey] = manifest,
                [CoreKeeperInstallationDiscovery.SaveProfilesRootPathKey] = profilesRoot,
                [CoreKeeperInstallationDiscovery.WorkshopContentRootPathKey] = Path.Combine(
                    library,
                    "steamapps",
                    "workshop",
                    "content",
                    "1621690"),
                [CoreKeeperInstallationDiscovery.ManualModsRootPathKey] = Path.Combine(
                    installRoot,
                    "CoreKeeper_Data",
                    "StreamingAssets",
                    "Mods"),
                [CoreKeeperInstallationDiscovery.GameSteamAppIdKey] = CoreKeeperInstallationDiscovery.GameSteamAppId
            });
    }

    private static string CreateWorldBundle(
        string profileRoot,
        string slot,
        byte[] worldBytes,
        byte[] infoBytes,
        byte[] generationBytes)
    {
        var worlds = Path.Combine(profileRoot, "worlds");
        var infos = Path.Combine(profileRoot, "worldinfos");
        var generation = Path.Combine(profileRoot, "worldgenparams");
        Directory.CreateDirectory(worlds);
        Directory.CreateDirectory(infos);
        Directory.CreateDirectory(generation);
        var worldPath = Path.Combine(worlds, $"{slot}.world.gzip");
        File.WriteAllBytes(worldPath, worldBytes);
        File.WriteAllBytes(
            Path.Combine(infos, $"{slot}.worldinfo"),
            infoBytes);
        File.WriteAllBytes(
            Path.Combine(generation, $"{slot}.json"),
            generationBytes);
        return worldPath;
    }

    private static byte[] ReadEntry(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName)
            ?? throw new Xunit.Sdk.XunitException(
                $"Missing archive entry {entryName}.");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void WriteEntry(
        ZipArchive archive,
        string entryName,
        byte[] bytes)
    {
        var entry = archive.CreateEntry(entryName);
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
