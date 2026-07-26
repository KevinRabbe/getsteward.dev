using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.SpaceEngineers.Tests;

public sealed class SpaceEngineersAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-space-engineers-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];

    [Fact]
    public void InstallationDiscoveryFindsSteamInstallAndSaveProfilesRoot()
    {
        var library = Path.Combine(_root, "steam-library");
        var installRoot = Path.Combine(library, "steamapps", "common", "SpaceEngineers");
        Directory.CreateDirectory(Path.Combine(installRoot, "Bin64"));
        File.WriteAllBytes(Path.Combine(installRoot, "Bin64", "SpaceEngineers.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_244850.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "\"AppState\" { \"buildid\" \"12345\" }");
        var saveRoot = Path.Combine(_root, "Roaming", "SpaceEngineers", "Saves");

        var installation = Assert.Single(
            SpaceEngineersInstallationDiscovery.DiscoverFromSteamLibraries([library], saveRoot));

        Assert.Equal(Path.GetFullPath(installRoot), installation.RootPath);
        Assert.Equal("steam", installation.Source);
        Assert.NotNull(installation.Metadata);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "Bin64", "SpaceEngineers.exe")),
            installation.Metadata[SpaceEngineersInstallationDiscovery.ClientExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(saveRoot),
            installation.Metadata[SpaceEngineersInstallationDiscovery.SaveProfilesRootPathKey]);
        Assert.Equal("244850", installation.Metadata[SpaceEngineersInstallationDiscovery.GameSteamAppIdKey]);
    }

    [Fact]
    public void WorldDiscoveryUsesSteamProfileAndDirectWorldDirectories()
    {
        var saveRoot = Path.Combine(_root, "saves");
        var profile = Path.Combine(saveRoot, "76561198000000000");
        var world = Path.Combine(profile, "Moon Base");
        CreateVanillaWorld(world);
        File.WriteAllText(Path.Combine(profile, "LastSession.sbl"), "profile-state");
        var offlineWorld = Path.Combine(saveRoot, "1234567891011", "Offline");
        CreateVanillaWorld(offlineWorld);

        var discovered = Assert.Single(
            SpaceEngineersWorldDiscovery.DiscoverFromSaveProfilesRoot(saveRoot));

        Assert.Equal("local:76561198000000000:Moon Base", discovered.Id);
        Assert.Equal("Moon Base", discovered.DisplayName);
        Assert.Equal(Path.GetFullPath(world), discovered.SourcePath);
    }

    [Fact]
    public void WorldDiscoveryRequiresThreeCurrentCoreFiles()
    {
        var saveRoot = Path.Combine(_root, "incomplete");
        var world = Path.Combine(saveRoot, "76561198000000000", "Broken");
        Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "Sandbox.sbc"), "x");
        File.WriteAllText(Path.Combine(world, "Sandbox_config.sbc"), VanillaConfig());

        Assert.Empty(SpaceEngineersWorldDiscovery.DiscoverFromSaveProfilesRoot(saveRoot));
    }

    [Fact]
    public void WorldDiscoveryIgnoresLinkedContentInsideBackup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var saveRoot = Path.Combine(_root, "backup-link");
        var world = Path.Combine(saveRoot, "76561198000000000", "World");
        CreateVanillaWorld(world);
        var backup = Path.Combine(world, "Backup");
        Directory.CreateDirectory(backup);
        var outside = Path.Combine(_root, "outside.sbc");
        File.WriteAllText(outside, "backup");
        var linked = Path.Combine(backup, "linked.sbc");
        File.CreateSymbolicLink(linked, outside);
        try
        {
            Assert.Single(SpaceEngineersWorldDiscovery.DiscoverFromSaveProfilesRoot(saveRoot));
        }
        finally
        {
            File.Delete(linked);
        }
    }

    [Fact]
    public void WorldDiscoveryRejectsLinkedCurrentMember()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var saveRoot = Path.Combine(_root, "linked-current");
        var world = Path.Combine(saveRoot, "76561198000000000", "World");
        CreateVanillaWorld(world);
        var outside = Path.Combine(_root, "outside.vx2");
        File.WriteAllBytes(outside, [1, 2]);
        var linked = Path.Combine(world, "Planet.vx2");
        File.CreateSymbolicLink(linked, outside);
        try
        {
            Assert.Empty(SpaceEngineersWorldDiscovery.DiscoverFromSaveProfilesRoot(saveRoot));
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
        var world = Path.Combine(_root, "environment-world");
        CreateVanillaWorld(world);

        var environment = SpaceEngineersEnvironment.Inspect(
            installation,
            new DetectedWorld("local:1:World", "World", world));

        Assert.Equal(1, environment.SchemaVersion);
        Assert.Equal("space-engineers", environment.AdapterId);
        Assert.Equal("24680", environment.GameVersion);
        Assert.Empty(environment.Components);
        Assert.Empty(environment.Configuration);
    }

    [Fact]
    public void EnvironmentInspectionRejectsEnabledMods()
    {
        var installation = CreateInstallation("24680");
        var world = Path.Combine(_root, "modded-world");
        CreateVanillaWorld(world);
        File.WriteAllText(
            Path.Combine(world, SpaceEngineersWorldDiscovery.SandboxConfigFileName),
            "<MyObjectBuilder_WorldConfiguration><Mods><ModItem><PublishedFileId>123</PublishedFileId></ModItem></Mods></MyObjectBuilder_WorldConfiguration>");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SpaceEngineersEnvironment.Inspect(
                installation,
                new DetectedWorld("local:1:World", "World", world)));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentInspectionRejectsMissingModsBoundary()
    {
        var installation = CreateInstallation("24680");
        var world = Path.Combine(_root, "ambiguous-world");
        CreateVanillaWorld(world);
        File.WriteAllText(
            Path.Combine(world, SpaceEngineersWorldDiscovery.SandboxConfigFileName),
            "<MyObjectBuilder_WorldConfiguration><Settings /></MyObjectBuilder_WorldConfiguration>");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SpaceEngineersEnvironment.Inspect(
                installation,
                new DetectedWorld("local:1:World", "World", world)));

        Assert.Contains("cannot prove a vanilla environment", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedWorldConfigFailsBeforeParsing()
    {
        var installation = CreateInstallation("24680");
        var world = Path.Combine(_root, "large-config");
        CreateVanillaWorld(world);
        var config = Path.Combine(world, SpaceEngineersWorldDiscovery.SandboxConfigFileName);
        using (var stream = new FileStream(config, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(SpaceEngineersEnvironment.MaximumWorldConfigBytes + 1);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SpaceEngineersEnvironment.Inspect(
                installation,
                new DetectedWorld("local:1:World", "World", world)));

        Assert.Contains("metadata safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedSteamManifestFailsBeforeParsing()
    {
        var installation = CreateInstallation("12345");
        var manifest = installation.Metadata![SpaceEngineersInstallationDiscovery.SteamManifestPathKey];
        using (var stream = new FileStream(manifest, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(SpaceEngineersEnvironment.MaximumSteamManifestBytes + 1);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SpaceEngineersEnvironment.ReadRequiredBuildId(installation));

        Assert.Contains("metadata safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureIncludesCurrentWorldAndExcludesBackupHistory()
    {
        var world = Path.Combine(_root, "capture-world");
        CreateVanillaWorld(world);
        Directory.CreateDirectory(Path.Combine(world, "Storage"));
        File.WriteAllBytes(Path.Combine(world, "Storage", "current.bin"), [7, 8]);
        Directory.CreateDirectory(Path.Combine(world, "Backup", "2026-07-20"));
        File.WriteAllBytes(Path.Combine(world, "Backup", "2026-07-20", "Sandbox.sbc"), [9]);
        var adapter = new SpaceEngineersAdapter();
        var detected = new DetectedWorld("local:1:World", "World", world);

        var captured = await adapter.CaptureDetectedWorldAsync(
            CreateInstallation("13579"),
            detected);
        _packages.Add(captured.Package.Path);

        using var archive = ZipFile.OpenRead(captured.Package.Path);
        var names = archive.Entries
            .Select(entry => entry.FullName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Contains("Sandbox.sbc", names);
        Assert.Contains("Sandbox_config.sbc", names);
        Assert.Contains("SANDBOX_0_0_0_.sbs", names);
        Assert.Contains("Storage/current.bin", names);
        Assert.DoesNotContain(names, name => name.StartsWith("Backup/", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(Path.Combine(world, "Backup", "2026-07-20", "Sandbox.sbc")));
    }

    [Fact]
    public async Task CaptureRestoreCaptureRoundTripPreservesWorldFiles()
    {
        var installation = CreateInstallation("13579");
        var source = Path.Combine(_root, "roundtrip-world");
        CreateVanillaWorld(source);
        var sandboxBytes = Enumerable.Range(0, 2048).Select(index => (byte)(index % 251)).ToArray();
        var sectorBytes = Enumerable.Range(0, 8192).Select(index => (byte)(index % 239)).ToArray();
        var voxelBytes = Enumerable.Range(0, 4096).Select(index => (byte)(index % 233)).ToArray();
        File.WriteAllBytes(Path.Combine(source, SpaceEngineersWorldDiscovery.SandboxFileName), sandboxBytes);
        File.WriteAllBytes(Path.Combine(source, SpaceEngineersWorldDiscovery.SectorFileName), sectorBytes);
        File.WriteAllBytes(Path.Combine(source, "Planet.vx2"), voxelBytes);
        var adapter = new SpaceEngineersAdapter();
        var detected = new DetectedWorld("local:1:World", "World", source);
        var environment = SpaceEngineersEnvironment.Inspect(installation, detected);
        var imported = await adapter.CaptureDetectedWorldAsync(installation, detected);
        _packages.Add(imported.Package.Path);
        var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);
        var workspace = prepared.WorkingDirectory;

        try
        {
            await adapter.RestoreStateAsync(prepared, imported.Package);
            var restored = Path.Combine(workspace, "save", "world");
            Assert.Equal(sandboxBytes, File.ReadAllBytes(Path.Combine(restored, SpaceEngineersWorldDiscovery.SandboxFileName)));
            Assert.Equal(sectorBytes, File.ReadAllBytes(Path.Combine(restored, SpaceEngineersWorldDiscovery.SectorFileName)));
            Assert.Equal(voxelBytes, File.ReadAllBytes(Path.Combine(restored, "Planet.vx2")));

            var recaptured = await adapter.CaptureStateAsync(prepared);
            _packages.Add(recaptured.Package.Path);
            using var archive = ZipFile.OpenRead(recaptured.Package.Path);
            Assert.Equal(sandboxBytes, ReadEntry(archive, SpaceEngineersWorldDiscovery.SandboxFileName));
            Assert.Equal(sectorBytes, ReadEntry(archive, SpaceEngineersWorldDiscovery.SectorFileName));
            Assert.Equal(voxelBytes, ReadEntry(archive, "Planet.vx2"));

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
        var required = VanillaEnvironment("13579");
        var package = Path.Combine(_root, "traversal.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "../escape.sbc", [1]);
        }
        _packages.Add(package);
        var adapter = new SpaceEngineersAdapter();
        var prepared = await adapter.PrepareEnvironmentAsync(installation, required);

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
    public async Task RestoreRejectsNativeBackupHistory()
    {
        var installation = CreateInstallation("13579");
        var required = VanillaEnvironment("13579");
        var package = Path.Combine(_root, "backup.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteRequiredWorldEntries(archive);
            WriteEntry(archive, "Backup/old/Sandbox.sbc", [1]);
        }
        _packages.Add(package);
        var adapter = new SpaceEngineersAdapter();
        var prepared = await adapter.PrepareEnvironmentAsync(installation, required);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                adapter.RestoreStateAsync(prepared, new StatePackage("bad", package)));
            Assert.Contains("Backup recovery history", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await adapter.FinalizePreparedWorldAsync(prepared, PreparedWorldDisposition.Discard);
        }
    }

    [Fact]
    public async Task RestoreRejectsModdedWorldPackage()
    {
        var installation = CreateInstallation("13579");
        var required = VanillaEnvironment("13579");
        var package = Path.Combine(_root, "modded.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            WriteEntry(archive, SpaceEngineersWorldDiscovery.SandboxFileName, [1]);
            WriteEntry(
                archive,
                SpaceEngineersWorldDiscovery.SandboxConfigFileName,
                System.Text.Encoding.UTF8.GetBytes("<MyObjectBuilder_WorldConfiguration><Mods><ModItem /></Mods></MyObjectBuilder_WorldConfiguration>"));
            WriteEntry(archive, SpaceEngineersWorldDiscovery.SectorFileName, [2]);
        }
        _packages.Add(package);
        var adapter = new SpaceEngineersAdapter();
        var prepared = await adapter.PrepareEnvironmentAsync(installation, required);

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.RestoreStateAsync(prepared, new StatePackage("modded", package)));
            Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
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
        var required = VanillaEnvironment("22222");
        var adapter = new SpaceEngineersAdapter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.PrepareEnvironmentAsync(installation, required));

        Assert.Contains("does not match required build", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterAdvertisesOnlyExactGameVersion()
    {
        Assert.Equal(
            GameAdapterCapabilities.ExactGameVersion,
            new SpaceEngineersAdapter().Capabilities);
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var library = Path.Combine(_root, $"library-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(library, "steamapps", "common", "SpaceEngineers");
        Directory.CreateDirectory(Path.Combine(installRoot, "Bin64"));
        File.WriteAllBytes(Path.Combine(installRoot, "Bin64", "SpaceEngineers.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_244850.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, $"\"AppState\" {{ \"buildid\" \"{buildId}\" }}");
        var saveRoot = Path.Combine(_root, $"saves-{Guid.NewGuid():N}");

        return new GameInstallation(
            $"space-engineers:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SpaceEngineersInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(installRoot, "Bin64", "SpaceEngineers.exe"),
                [SpaceEngineersInstallationDiscovery.SteamManifestPathKey] = manifest,
                [SpaceEngineersInstallationDiscovery.SaveProfilesRootPathKey] = saveRoot,
                [SpaceEngineersInstallationDiscovery.GameSteamAppIdKey] = SpaceEngineersInstallationDiscovery.GameSteamAppId
            });
    }

    private static EnvironmentManifest VanillaEnvironment(string buildId)
        => new(
            1,
            "space-engineers",
            buildId,
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));

    private static void CreateVanillaWorld(string worldRoot)
    {
        Directory.CreateDirectory(worldRoot);
        File.WriteAllText(
            Path.Combine(worldRoot, SpaceEngineersWorldDiscovery.SandboxFileName),
            "sandbox");
        File.WriteAllText(
            Path.Combine(worldRoot, SpaceEngineersWorldDiscovery.SandboxConfigFileName),
            VanillaConfig());
        File.WriteAllText(
            Path.Combine(worldRoot, SpaceEngineersWorldDiscovery.SectorFileName),
            "sector");
    }

    private static string VanillaConfig()
        => "<MyObjectBuilder_WorldConfiguration><Mods /></MyObjectBuilder_WorldConfiguration>";

    private static byte[] ReadEntry(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName)
            ?? throw new Xunit.Sdk.XunitException($"Missing archive entry {entryName}.");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static void WriteRequiredWorldEntries(ZipArchive archive)
    {
        WriteEntry(archive, SpaceEngineersWorldDiscovery.SandboxFileName, [1]);
        WriteEntry(
            archive,
            SpaceEngineersWorldDiscovery.SandboxConfigFileName,
            System.Text.Encoding.UTF8.GetBytes(VanillaConfig()));
        WriteEntry(archive, SpaceEngineersWorldDiscovery.SectorFileName, [2]);
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
