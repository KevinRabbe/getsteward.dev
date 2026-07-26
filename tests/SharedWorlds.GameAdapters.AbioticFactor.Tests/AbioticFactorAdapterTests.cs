using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.AbioticFactor.Tests;

public sealed class AbioticFactorAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-abiotic-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];

    [Fact]
    public void InstallationDiscoveryFindsSteamInstallAndSaveRoot()
    {
        var library = Path.Combine(_root, "steam-library");
        var installRoot = Path.Combine(library, "steamapps", "common", "AbioticFactor");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "AbioticFactor.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_427410.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "\"AppState\" { \"buildid\" \"12345\" }");
        var saveRoot = Path.Combine(_root, "Local", "AbioticFactor", "Saved", "SaveGames");

        var installation = Assert.Single(
            AbioticFactorInstallationDiscovery.DiscoverFromSteamLibraries([library], saveRoot));

        Assert.Equal(Path.GetFullPath(installRoot), installation.RootPath);
        Assert.Equal("steam", installation.Source);
        Assert.NotNull(installation.Metadata);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "AbioticFactor.exe")),
            installation.Metadata[AbioticFactorInstallationDiscovery.ClientExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(saveRoot),
            installation.Metadata[AbioticFactorInstallationDiscovery.SaveGamesRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                installRoot,
                "AbioticFactor",
                "Binaries",
                "Win64")),
            installation.Metadata[AbioticFactorInstallationDiscovery.Ue4SsRootPathKey]);
        Assert.Equal("427410", installation.Metadata[AbioticFactorInstallationDiscovery.GameSteamAppIdKey]);
    }

    [Fact]
    public void WorldDiscoveryUsesCanonicalSteamProfileAndDirectWorldDirectories()
    {
        var saveRoot = Path.Combine(_root, "saves");
        var profile = Path.Combine(saveRoot, "76561198000000000");
        var world = Path.Combine(profile, "Worlds", "Cascade");
        Directory.CreateDirectory(Path.Combine(world, "PlayerData"));
        File.WriteAllBytes(Path.Combine(world, "WorldData.sav"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(world, "PlayerData", "Player_76561198000000000.sav"), [4]);
        File.WriteAllText(Path.Combine(world, "SandboxSettings.ini"), "[SandboxSettings]");
        File.WriteAllBytes(Path.Combine(profile, "Profile.sav"), [9]);
        File.WriteAllText(Path.Combine(profile, "Profile.ini"), "account");

        var invalidProfileWorld = Path.Combine(saveRoot, "not-steam", "Worlds", "Ignored");
        Directory.CreateDirectory(invalidProfileWorld);
        File.WriteAllBytes(Path.Combine(invalidProfileWorld, "WorldData.sav"), [7]);

        var discovered = Assert.Single(
            AbioticFactorWorldDiscovery.DiscoverFromSaveGamesRoot(saveRoot));

        Assert.Equal("local:76561198000000000:Cascade", discovered.Id);
        Assert.Equal("Cascade", discovered.DisplayName);
        Assert.Equal(Path.GetFullPath(world), discovered.SourcePath);
    }

    [Fact]
    public void WorldDiscoveryRequiresNonEmptySavData()
    {
        var saveRoot = Path.Combine(_root, "empty-world");
        var world = Path.Combine(saveRoot, "76561198000000000", "Worlds", "Empty");
        Directory.CreateDirectory(world);
        File.WriteAllBytes(Path.Combine(world, "WorldData.sav"), []);
        File.WriteAllText(Path.Combine(world, "SandboxSettings.ini"), "[SandboxSettings]");

        Assert.Empty(AbioticFactorWorldDiscovery.DiscoverFromSaveGamesRoot(saveRoot));
    }

    [Fact]
    public void WorldDiscoveryRejectsLinkedMember()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var saveRoot = Path.Combine(_root, "linked-world");
        var world = Path.Combine(saveRoot, "76561198000000000", "Worlds", "Cascade");
        Directory.CreateDirectory(world);
        File.WriteAllBytes(Path.Combine(world, "WorldData.sav"), [1]);
        var outside = Path.Combine(_root, "outside.sav");
        File.WriteAllBytes(outside, [2]);
        var linked = Path.Combine(world, "linked.sav");
        File.CreateSymbolicLink(linked, outside);
        try
        {
            Assert.Empty(AbioticFactorWorldDiscovery.DiscoverFromSaveGamesRoot(saveRoot));
        }
        finally
        {
            File.Delete(linked);
        }
    }

    [Fact]
    public void EnvironmentInspectionUsesExactSteamBuild()
    {
        var environment = AbioticFactorEnvironment.Inspect(CreateInstallation("24680"));

        Assert.Equal(1, environment.SchemaVersion);
        Assert.Equal("abiotic-factor", environment.AdapterId);
        Assert.Equal("24680", environment.GameVersion);
        Assert.Empty(environment.Components);
        Assert.Empty(environment.Configuration);
    }

    [Fact]
    public void EnvironmentInspectionRejectsUe4SsProxy()
    {
        var installation = CreateInstallation("24680");
        var loaderRoot = installation.Metadata![AbioticFactorInstallationDiscovery.Ue4SsRootPathKey];
        Directory.CreateDirectory(loaderRoot);
        File.WriteAllBytes(Path.Combine(loaderRoot, "dwmapi.dll"), [1]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AbioticFactorEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentInspectionRejectsUe4SsDirectory()
    {
        var installation = CreateInstallation("24680");
        var loaderRoot = installation.Metadata![AbioticFactorInstallationDiscovery.Ue4SsRootPathKey];
        Directory.CreateDirectory(Path.Combine(loaderRoot, "ue4ss", "Mods"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AbioticFactorEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedSteamManifestFailsBeforeParsing()
    {
        var installation = CreateInstallation("12345");
        var manifest = installation.Metadata![AbioticFactorInstallationDiscovery.SteamManifestPathKey];
        using (var stream = new FileStream(manifest, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(AbioticFactorEnvironment.MaximumSteamManifestBytes + 1);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AbioticFactorEnvironment.Inspect(installation));

        Assert.Contains("metadata safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureIncludesWorldPlayerAndSandboxStateButNotProfileState()
    {
        var profile = Path.Combine(_root, "profile", "76561198000000000");
        var world = Path.Combine(profile, "Worlds", "Cascade");
        Directory.CreateDirectory(Path.Combine(world, "PlayerData"));
        File.WriteAllBytes(Path.Combine(world, "WorldData.sav"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(world, "PlayerData", "Player_76561198000000000.sav"), [4, 5]);
        File.WriteAllText(Path.Combine(world, "SandboxSettings.ini"), "[SandboxSettings]");
        File.WriteAllBytes(Path.Combine(profile, "Profile.sav"), [9, 9]);
        var adapter = new AbioticFactorAdapter();
        var detected = new DetectedWorld(
            "local:76561198000000000:Cascade",
            "Cascade",
            world);

        var captured = await adapter.CaptureDetectedWorldAsync(
            CreateInstallation("13579"),
            detected);
        _packages.Add(captured.Package.Path);

        using var archive = ZipFile.OpenRead(captured.Package.Path);
        var names = archive.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[]
            {
                "PlayerData/Player_76561198000000000.sav",
                "SandboxSettings.ini",
                "WorldData.sav"
            },
            names);
        Assert.DoesNotContain("Profile.sav", names);
        Assert.Equal([9, 9], File.ReadAllBytes(Path.Combine(profile, "Profile.sav")));
    }

    [Fact]
    public async Task CaptureRestoreCaptureRoundTripPreservesNestedWorldFiles()
    {
        var installation = CreateInstallation("13579");
        var environment = AbioticFactorEnvironment.Inspect(installation);
        var world = Path.Combine(_root, "roundtrip", "Cascade");
        Directory.CreateDirectory(Path.Combine(world, "PlayerData"));
        var worldBytes = Enumerable.Range(0, 8192).Select(index => (byte)(index % 251)).ToArray();
        var playerBytes = Enumerable.Range(0, 1024).Select(index => (byte)(index % 239)).ToArray();
        File.WriteAllBytes(Path.Combine(world, "WorldData.sav"), worldBytes);
        File.WriteAllBytes(Path.Combine(world, "PlayerData", "Player_1.sav"), playerBytes);
        File.WriteAllText(Path.Combine(world, "SandboxSettings.ini"), "[SandboxSettings]\nGameDifficulty=1");
        var adapter = new AbioticFactorAdapter();
        var detected = new DetectedWorld("local:1:Cascade", "Cascade", world);
        var imported = await adapter.CaptureDetectedWorldAsync(installation, detected);
        _packages.Add(imported.Package.Path);
        var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);
        var workspace = prepared.WorkingDirectory;

        try
        {
            await adapter.RestoreStateAsync(prepared, imported.Package);
            var restored = Path.Combine(workspace, "save", "world");
            Assert.Equal(worldBytes, File.ReadAllBytes(Path.Combine(restored, "WorldData.sav")));
            Assert.Equal(playerBytes, File.ReadAllBytes(Path.Combine(restored, "PlayerData", "Player_1.sav")));

            var recaptured = await adapter.CaptureStateAsync(prepared);
            _packages.Add(recaptured.Package.Path);
            using var archive = ZipFile.OpenRead(recaptured.Package.Path);
            Assert.Equal(worldBytes, ReadEntry(archive, "WorldData.sav"));
            Assert.Equal(playerBytes, ReadEntry(archive, "PlayerData/Player_1.sav"));

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
    public async Task RestoreRejectsTraversalEntry()
    {
        var installation = CreateInstallation("13579");
        var environment = AbioticFactorEnvironment.Inspect(installation);
        var package = Path.Combine(_root, "traversal.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "../escape.sav", [1]);
        }
        _packages.Add(package);
        var adapter = new AbioticFactorAdapter();
        var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                adapter.RestoreStateAsync(prepared, new StatePackage("bad", package)));
        }
        finally
        {
            await adapter.FinalizePreparedWorldAsync(prepared, PreparedWorldDisposition.Discard);
        }
    }

    [Fact]
    public async Task RestoreRejectsCaseCollidingEntries()
    {
        var installation = CreateInstallation("13579");
        var environment = AbioticFactorEnvironment.Inspect(installation);
        var package = Path.Combine(_root, "collision.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "WorldData.sav", [1]);
            WriteEntry(archive, "worlddata.sav", [2]);
        }
        _packages.Add(package);
        var adapter = new AbioticFactorAdapter();
        var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                adapter.RestoreStateAsync(prepared, new StatePackage("bad", package)));
            Assert.Contains("colliding path", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await adapter.FinalizePreparedWorldAsync(prepared, PreparedWorldDisposition.Discard);
        }
    }

    [Fact]
    public async Task RestoreRejectsPackageWithoutWorldSavData()
    {
        var installation = CreateInstallation("13579");
        var environment = AbioticFactorEnvironment.Inspect(installation);
        var package = Path.Combine(_root, "no-world.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "SandboxSettings.ini", [1]);
        }
        _packages.Add(package);
        var adapter = new AbioticFactorAdapter();
        var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                adapter.RestoreStateAsync(prepared, new StatePackage("bad", package)));
            Assert.Contains("World .sav data", exception.Message, StringComparison.Ordinal);
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
            "abiotic-factor",
            "22222",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var adapter = new AbioticFactorAdapter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.PrepareEnvironmentAsync(installation, required));

        Assert.Contains("does not match required build", exception.Message, StringComparison.Ordinal);
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
        var installRoot = Path.Combine(library, "steamapps", "common", "AbioticFactor");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "AbioticFactor.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_427410.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, $"\"AppState\" {{ \"buildid\" \"{buildId}\" }}");
        var saveRoot = Path.Combine(_root, $"saves-{Guid.NewGuid():N}");

        return new GameInstallation(
            $"abiotic-factor:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [AbioticFactorInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(installRoot, "AbioticFactor.exe"),
                [AbioticFactorInstallationDiscovery.SteamManifestPathKey] = manifest,
                [AbioticFactorInstallationDiscovery.SaveGamesRootPathKey] = saveRoot,
                [AbioticFactorInstallationDiscovery.Ue4SsRootPathKey] = Path.Combine(
                    installRoot,
                    "AbioticFactor",
                    "Binaries",
                    "Win64"),
                [AbioticFactorInstallationDiscovery.GameSteamAppIdKey] = AbioticFactorInstallationDiscovery.GameSteamAppId
            });
    }

    private static byte[] ReadEntry(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName)
            ?? throw new Xunit.Sdk.XunitException($"Missing archive entry {entryName}.");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string entryName, byte[] bytes)
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
