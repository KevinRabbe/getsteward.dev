using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.AbioticFactor.Tests;

public sealed class AbioticFactorAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-abiotic-factor-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];

    [Fact]
    public void InstallationDiscoveryFindsSteamInstallationAndSaveRoot()
    {
        var library = Path.Combine(_root, "steam-library");
        var installRoot = Path.Combine(
            library,
            "steamapps",
            "common",
            "AbioticFactor");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "AbioticFactor.exe"), [1]);
        var manifest = Path.Combine(
            library,
            "steamapps",
            "appmanifest_427410.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            "\"AppState\" { \"appid\" \"427410\" \"buildid\" \"12345\" }");
        var saveRoot = Path.Combine(_root, "save-root");

        var installation = Assert.Single(
            AbioticFactorInstallationDiscovery.DiscoverFromSteamLibraries(
                [library],
                saveRoot));

        Assert.Equal(Path.GetFullPath(installRoot), installation.RootPath);
        Assert.Equal("steam", installation.Source);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "AbioticFactor.exe")),
            installation.Metadata![AbioticFactorInstallationDiscovery.ClientExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(saveRoot),
            installation.Metadata[AbioticFactorInstallationDiscovery.SaveGamesRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                installRoot,
                "AbioticFactor",
                "Binaries",
                "Win64")),
            installation.Metadata[AbioticFactorInstallationDiscovery.Win64RootPathKey]);
    }

    [Fact]
    public void WorldDiscoveryFindsOnlySteamProfileWorldsWithMetadata()
    {
        var saveRoot = Path.Combine(_root, "world-discovery");
        var valid = Path.Combine(
            saveRoot,
            "76561198000000000",
            "Worlds",
            "Cascade");
        Directory.CreateDirectory(valid);
        File.WriteAllBytes(
            Path.Combine(valid, AbioticFactorWorldDiscovery.MetadataFileName),
            [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(valid, "PlayerData"));
        File.WriteAllBytes(Path.Combine(valid, "PlayerData", "Player_1.sav"), [4]);

        var invalidProfile = Path.Combine(
            saveRoot,
            "Server",
            "Worlds",
            "Dedicated");
        Directory.CreateDirectory(invalidProfile);
        File.WriteAllBytes(
            Path.Combine(invalidProfile, AbioticFactorWorldDiscovery.MetadataFileName),
            [5]);

        var missingMetadata = Path.Combine(
            saveRoot,
            "76561198000000000",
            "Worlds",
            "Incomplete");
        Directory.CreateDirectory(missingMetadata);
        File.WriteAllBytes(Path.Combine(missingMetadata, "WorldSave_Facility.sav"), [6]);

        var detected = Assert.Single(
            AbioticFactorWorldDiscovery.DiscoverFromSaveRoot(saveRoot));

        Assert.Equal("Cascade", detected.DisplayName);
        Assert.Equal("local:76561198000000000:Cascade", detected.Id);
        Assert.Equal(Path.GetFullPath(valid), detected.SourcePath);
    }

    [Fact]
    public void WorldDiscoveryRequiresCanonicalSteamId()
    {
        Assert.True(AbioticFactorWorldDiscovery.TryGetSteamId(
            "76561198000000000",
            out var steamId));
        Assert.Equal(76561198000000000UL, steamId);
        Assert.False(AbioticFactorWorldDiscovery.TryGetSteamId(
            "076561198000000000",
            out _));
        Assert.False(AbioticFactorWorldDiscovery.TryGetSteamId("Server", out _));
    }

    [Fact]
    public void WorldDiscoveryRejectsLinkedMetadata()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var saveRoot = Path.Combine(_root, "linked-metadata");
        var world = Path.Combine(
            saveRoot,
            "76561198000000000",
            "Worlds",
            "Linked");
        Directory.CreateDirectory(world);
        var outside = Path.Combine(_root, "outside.sav");
        File.WriteAllBytes(outside, [1, 2, 3]);
        var link = Path.Combine(world, AbioticFactorWorldDiscovery.MetadataFileName);
        File.CreateSymbolicLink(link, outside);
        try
        {
            Assert.Empty(AbioticFactorWorldDiscovery.DiscoverFromSaveRoot(saveRoot));
        }
        finally
        {
            File.Delete(link);
        }
    }

    [Fact]
    public void EnvironmentInspectionUsesExactSteamBuild()
    {
        var installation = CreateInstallation("24680");

        var environment = AbioticFactorEnvironment.Inspect(installation);

        Assert.Equal(1, environment.SchemaVersion);
        Assert.Equal("abiotic-factor", environment.AdapterId);
        Assert.Equal("24680", environment.GameVersion);
        Assert.Empty(environment.Components);
    }

    [Fact]
    public void EnvironmentInspectionRejectsUe4ssMarker()
    {
        var installation = CreateInstallation("24680");
        var win64 = installation.Metadata![
            AbioticFactorInstallationDiscovery.Win64RootPathKey];
        Directory.CreateDirectory(win64);
        File.WriteAllBytes(Path.Combine(win64, "dwmapi.dll"), [1]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AbioticFactorEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentInspectionRejectsLinkedUe4ssDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var installation = CreateInstallation("24680");
        var win64 = installation.Metadata![
            AbioticFactorInstallationDiscovery.Win64RootPathKey];
        Directory.CreateDirectory(win64);
        var outside = Path.Combine(_root, "outside-ue4ss");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(win64, "ue4ss");
        Directory.CreateSymbolicLink(link, outside);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                AbioticFactorEnvironment.Inspect(installation));
            Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void OversizedSteamManifestFailsClosed()
    {
        var installation = CreateInstallation("12345");
        var manifest = installation.Metadata![
            AbioticFactorInstallationDiscovery.SteamManifestPathKey];
        using (var stream = new FileStream(
                   manifest,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(AbioticFactorEnvironment.MaximumSteamManifestBytes + 1);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AbioticFactorEnvironment.Inspect(installation));

        Assert.Contains("metadata safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRestoreCapturePreservesCompleteWorldTree()
    {
        var installation = CreateInstallation("13579");
        var environment = AbioticFactorEnvironment.Inspect(installation);
        var world = CreateWorld(installation, "Cascade");
        var adapter = new AbioticFactorAdapter();
        var detected = Assert.Single(
            AbioticFactorWorldDiscovery.DiscoverFromSaveRoot(
                installation.Metadata![AbioticFactorInstallationDiscovery.SaveGamesRootPathKey]));
        var expected = ReadTree(world);

        var imported = await adapter.CaptureDetectedWorldAsync(installation, detected);
        _packages.Add(imported.Package.Path);
        Assert.Equal(expected, ReadZip(imported.Package.Path));
        Assert.Equal(expected, ReadTree(world));

        var prepared = await adapter.PrepareEnvironmentAsync(
            installation,
            environment);
        var workspace = prepared.WorkingDirectory;
        try
        {
            await adapter.RestoreStateAsync(prepared, imported.Package);
            Assert.Equal(
                expected,
                ReadTree(Path.Combine(workspace, "world")));

            var recaptured = await adapter.CaptureStateAsync(prepared);
            _packages.Add(recaptured.Package.Path);
            Assert.Equal(expected, ReadZip(recaptured.Package.Path));

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
    public async Task CaptureRejectsLinkedNestedWorldFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var installation = CreateInstallation("13579");
        var world = CreateWorld(installation, "LinkedWorld");
        var outside = Path.Combine(_root, "outside-player.sav");
        File.WriteAllBytes(outside, [8, 8, 8]);
        var linked = Path.Combine(world, "PlayerData", "Player_Linked.sav");
        File.CreateSymbolicLink(linked, outside);
        var detected = Assert.Single(
            AbioticFactorWorldDiscovery.DiscoverFromSaveRoot(
                installation.Metadata![AbioticFactorInstallationDiscovery.SaveGamesRootPathKey]));
        var adapter = new AbioticFactorAdapter();

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.CaptureDetectedWorldAsync(installation, detected));
            Assert.Contains("non-linked file", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(linked);
        }
    }

    [Fact]
    public async Task RestoreRejectsZipSlipPackage()
    {
        var installation = CreateInstallation("13579");
        var environment = AbioticFactorEnvironment.Inspect(installation);
        var package = Path.Combine(_root, "unsafe.zip");
        Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(
                archive,
                AbioticFactorWorldDiscovery.MetadataFileName,
                [1]);
            WriteEntry(archive, "../escape.sav", [2]);
        }

        var adapter = new AbioticFactorAdapter();
        var prepared = await adapter.PrepareEnvironmentAsync(
            installation,
            environment);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                adapter.RestoreStateAsync(
                    prepared,
                    new StatePackage("unsafe", package)));
        }
        finally
        {
            await adapter.FinalizePreparedWorldAsync(
                prepared,
                PreparedWorldDisposition.Discard);
        }
    }

    [Fact]
    public async Task RestoreRejectsPackageWithoutWorldMetadata()
    {
        var installation = CreateInstallation("13579");
        var environment = AbioticFactorEnvironment.Inspect(installation);
        var package = Path.Combine(_root, "missing-metadata.zip");
        Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "WorldSave_Facility.sav", [1]);
        }

        var adapter = new AbioticFactorAdapter();
        var prepared = await adapter.PrepareEnvironmentAsync(
            installation,
            environment);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                adapter.RestoreStateAsync(
                    prepared,
                    new StatePackage("missing", package)));
            Assert.Contains("WorldSave_MetaData.sav", exception.Message, StringComparison.Ordinal);
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
            "abiotic-factor",
            "22222",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var adapter = new AbioticFactorAdapter();

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
            new AbioticFactorAdapter().Capabilities);
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var library = Path.Combine(_root, $"library-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(
            library,
            "steamapps",
            "common",
            "AbioticFactor");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "AbioticFactor.exe"), [1]);
        var manifest = Path.Combine(
            library,
            "steamapps",
            "appmanifest_427410.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"appid\" \"427410\" \"buildid\" \"{buildId}\" }}");
        var saveRoot = Path.Combine(_root, $"saves-{Guid.NewGuid():N}");
        var win64 = Path.Combine(
            installRoot,
            "AbioticFactor",
            "Binaries",
            "Win64");

        return new GameInstallation(
            $"abiotic-factor:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AbioticFactorInstallationDiscovery.ClientExecutablePathKey] =
                    Path.Combine(installRoot, "AbioticFactor.exe"),
                [AbioticFactorInstallationDiscovery.SteamManifestPathKey] = manifest,
                [AbioticFactorInstallationDiscovery.SaveGamesRootPathKey] = saveRoot,
                [AbioticFactorInstallationDiscovery.Win64RootPathKey] = win64,
                [AbioticFactorInstallationDiscovery.GameSteamAppIdKey] =
                    AbioticFactorInstallationDiscovery.GameSteamAppId
            });
    }

    private static string CreateWorld(
        GameInstallation installation,
        string worldName)
    {
        var saveRoot = installation.Metadata![
            AbioticFactorInstallationDiscovery.SaveGamesRootPathKey];
        var world = Path.Combine(
            saveRoot,
            "76561198000000000",
            "Worlds",
            worldName);
        Directory.CreateDirectory(Path.Combine(world, "PlayerData"));
        File.WriteAllBytes(
            Path.Combine(world, AbioticFactorWorldDiscovery.MetadataFileName),
            [1, 2, 3, 4]);
        File.WriteAllBytes(
            Path.Combine(world, "WorldSave_Facility.sav"),
            [5, 6, 7]);
        File.WriteAllBytes(
            Path.Combine(world, "WorldSave_Facility_Labs.sav"),
            [8, 9]);
        File.WriteAllBytes(
            Path.Combine(world, "PlayerData", "Player_76561198000000000.sav"),
            [10, 11, 12]);
        File.WriteAllText(
            Path.Combine(world, "SandboxSettings.ini"),
            "[SandboxSettings]\nDifficulty=1\n");
        return world;
    }

    private static IReadOnlyDictionary<string, byte[]> ReadTree(string root)
    {
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path)
                    .Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllBytes,
                StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, byte[]> ReadZip(string path)
    {
        using var archive = ZipFile.OpenRead(path);
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
