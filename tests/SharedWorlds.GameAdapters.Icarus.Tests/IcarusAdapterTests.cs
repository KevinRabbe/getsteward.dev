using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.Icarus.Tests;

public sealed class IcarusAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-icarus-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];

    [Fact]
    public void InstallationDiscoveryFindsSteamIcarusAndOwnedRoots()
    {
        var library = Path.Combine(_root, "steam-library");
        var installRoot = Path.Combine(library, "steamapps", "common", "Icarus");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Icarus.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_1149460.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            "\"AppState\" { \"appid\" \"1149460\" \"buildid\" \"12345\" }");
        var playerDataRoot = Path.Combine(_root, "player-data");

        var installation = Assert.Single(
            IcarusInstallationDiscovery.DiscoverFromSteamLibraries(
                [library],
                playerDataRoot));

        Assert.Equal(Path.GetFullPath(installRoot), installation.RootPath);
        Assert.Equal("steam", installation.Source);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "Icarus.exe")),
            installation.Metadata![IcarusInstallationDiscovery.ClientExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(playerDataRoot),
            installation.Metadata[IcarusInstallationDiscovery.PlayerDataRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "Icarus", "Content", "Paks", "mods")),
            installation.Metadata[IcarusInstallationDiscovery.ModsRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(manifest),
            installation.Metadata[IcarusInstallationDiscovery.SteamManifestPathKey]);
    }

    [Fact]
    public void WorldDiscoveryFindsOnlyCurrentProspectsAndExcludesPlayerStateAndBackups()
    {
        var playerDataRoot = Path.Combine(_root, "worlds");
        var profile = Path.Combine(playerDataRoot, "76561198000000000");
        var prospects = Path.Combine(profile, "Prospects");
        Directory.CreateDirectory(prospects);
        File.WriteAllBytes(Path.Combine(prospects, "OlympusHome.json"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(prospects, "OlympusHome.json.backup_1"), [4]);
        File.WriteAllBytes(Path.Combine(prospects, "OlympusHome.json.backup_10"), [5]);
        File.WriteAllBytes(Path.Combine(profile, "Characters.json"), [6]);
        File.WriteAllBytes(Path.Combine(profile, "Profile.json"), [7]);
        File.WriteAllBytes(Path.Combine(profile, "MetaInventory.json"), [8]);

        var invalidProfile = Path.Combine(playerDataRoot, "not-a-steamid", "Prospects");
        Directory.CreateDirectory(invalidProfile);
        File.WriteAllBytes(Path.Combine(invalidProfile, "Wrong.json"), [9]);

        var detected = Assert.Single(
            IcarusWorldDiscovery.DiscoverFromPlayerDataRoot(playerDataRoot));

        Assert.Equal("OlympusHome", detected.DisplayName);
        Assert.Equal("local:76561198000000000:OlympusHome", detected.Id);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(prospects, "OlympusHome.json")),
            detected.SourcePath);
    }

    [Fact]
    public void CurrentProspectPathRejectsBackupNames()
    {
        Assert.True(IcarusWorldDiscovery.IsCurrentProspectPath("World.json"));
        Assert.False(IcarusWorldDiscovery.IsCurrentProspectPath("World.json.backup_1"));
        Assert.False(IcarusWorldDiscovery.IsCurrentProspectPath("World.backup_1.json"));
        Assert.False(IcarusWorldDiscovery.IsCurrentProspectPath("World.json.bak"));
    }

    [Fact]
    public void WorldDiscoveryRejectsLinkedCurrentProspect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var playerDataRoot = Path.Combine(_root, "linked");
        var prospects = Path.Combine(
            playerDataRoot,
            "76561198000000000",
            "Prospects");
        Directory.CreateDirectory(prospects);
        var outside = Path.Combine(_root, "outside.json");
        File.WriteAllBytes(outside, [1, 2, 3]);
        var link = Path.Combine(prospects, "Linked.json");
        File.CreateSymbolicLink(link, outside);
        try
        {
            Assert.Empty(IcarusWorldDiscovery.DiscoverFromPlayerDataRoot(playerDataRoot));
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
        var environment = IcarusEnvironment.Inspect(installation);

        Assert.Equal(1, environment.SchemaVersion);
        Assert.Equal("icarus", environment.AdapterId);
        Assert.Equal("24680", environment.GameVersion);
        Assert.Empty(environment.Components);
        Assert.Empty(environment.Configuration);
    }

    [Fact]
    public void EnvironmentInspectionRejectsActivePaksMods()
    {
        var installation = CreateInstallation("24680");
        var mods = installation.Metadata![IcarusInstallationDiscovery.ModsRootPathKey];
        Directory.CreateDirectory(mods);
        File.WriteAllBytes(Path.Combine(mods, "Example_P.pak"), [1]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            IcarusEnvironment.Inspect(installation));

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
        var mods = installation.Metadata![IcarusInstallationDiscovery.ModsRootPathKey];
        var outside = Path.Combine(_root, "outside-mods");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.GetDirectoryName(mods)!);
        Directory.CreateSymbolicLink(mods, outside);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                IcarusEnvironment.Inspect(installation));
            Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(mods);
        }
    }

    [Fact]
    public void OversizedSteamManifestFailsClosed()
    {
        var installation = CreateInstallation("12345");
        var manifest = installation.Metadata![IcarusInstallationDiscovery.SteamManifestPathKey];
        using (var stream = new FileStream(
                   manifest,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(IcarusEnvironment.MaximumSteamManifestBytes + 1);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            IcarusEnvironment.Inspect(installation));

        Assert.Contains("metadata safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRestoreCapturePreservesOnlyCurrentProspectBytes()
    {
        var installation = CreateInstallation("13579");
        var environment = IcarusEnvironment.Inspect(installation);
        var playerDataRoot = installation.Metadata![IcarusInstallationDiscovery.PlayerDataRootPathKey];
        var profile = Path.Combine(playerDataRoot, "76561198000000000");
        var prospects = Path.Combine(profile, "Prospects");
        Directory.CreateDirectory(prospects);
        var source = Path.Combine(prospects, "OlympusHome.json");
        var bytes = Enumerable.Range(0, 16384)
            .Select(index => (byte)((index * 17) % 251))
            .ToArray();
        File.WriteAllBytes(source, bytes);
        File.WriteAllBytes(Path.Combine(prospects, "OlympusHome.json.backup_1"), [9, 9, 9]);
        File.WriteAllBytes(Path.Combine(profile, "Characters.json"), [8, 8, 8]);
        File.WriteAllBytes(Path.Combine(profile, "Profile.json"), [7, 7, 7]);
        var detected = Assert.Single(
            IcarusWorldDiscovery.DiscoverFromPlayerDataRoot(playerDataRoot));
        var adapter = new IcarusAdapter();

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
                File.ReadAllBytes(Path.Combine(workspace, "save", "world.json")));

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
        var environment = IcarusEnvironment.Inspect(installation);
        var empty = Path.Combine(_root, "empty.json");
        File.WriteAllBytes(empty, []);
        var adapter = new IcarusAdapter();
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
            "icarus",
            "22222",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var adapter = new IcarusAdapter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.PrepareEnvironmentAsync(installation, required));

        Assert.Contains(
            "does not match required build",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileIdentityRequiresCanonicalSteamId()
    {
        Assert.True(IcarusWorldDiscovery.TryGetSteamId(
            "76561198000000000",
            out var steamId));
        Assert.Equal(76561198000000000UL, steamId);
        Assert.False(IcarusWorldDiscovery.TryGetSteamId(
            "076561198000000000",
            out _));
        Assert.False(IcarusWorldDiscovery.TryGetSteamId(
            "User_76561198000000000",
            out _));
    }

    [Fact]
    public void AdapterAdvertisesOnlyExactGameVersion()
    {
        Assert.Equal(
            GameAdapterCapabilities.ExactGameVersion,
            new IcarusAdapter().Capabilities);
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var library = Path.Combine(_root, $"library-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(library, "steamapps", "common", "Icarus");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Icarus.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_1149460.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"appid\" \"1149460\" \"buildid\" \"{buildId}\" }}");
        var playerDataRoot = Path.Combine(_root, $"player-data-{Guid.NewGuid():N}");

        return new GameInstallation(
            $"icarus:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [IcarusInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(
                    installRoot,
                    "Icarus.exe"),
                [IcarusInstallationDiscovery.SteamManifestPathKey] = manifest,
                [IcarusInstallationDiscovery.PlayerDataRootPathKey] = playerDataRoot,
                [IcarusInstallationDiscovery.ModsRootPathKey] = Path.Combine(
                    installRoot,
                    "Icarus",
                    "Content",
                    "Paks",
                    "mods"),
                [IcarusInstallationDiscovery.GameSteamAppIdKey] =
                    IcarusInstallationDiscovery.GameSteamAppId
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
