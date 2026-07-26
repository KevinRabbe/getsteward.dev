using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.Raft.Tests;

public sealed class RaftAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-raft-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];

    [Fact]
    public void InstallationDiscoveryFindsSteamRaftAndOwnedRoots()
    {
        var library = Path.Combine(_root, "steam-library");
        var installRoot = Path.Combine(library, "steamapps", "common", "Raft");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Raft.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_648800.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            "\"AppState\" { \"appid\" \"648800\" \"buildid\" \"12345\" }");
        var userRoot = Path.Combine(_root, "users");
        var rmlRoot = Path.Combine(_root, "rml");

        var installation = Assert.Single(
            RaftInstallationDiscovery.DiscoverFromSteamLibraries(
                [library],
                userRoot,
                rmlRoot));

        Assert.Equal(Path.GetFullPath(installRoot), installation.RootPath);
        Assert.Equal("steam", installation.Source);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "Raft.exe")),
            installation.Metadata![RaftInstallationDiscovery.ClientExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(userRoot),
            installation.Metadata[RaftInstallationDiscovery.UserRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "mods")),
            installation.Metadata[RaftInstallationDiscovery.ModsRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(rmlRoot),
            installation.Metadata[RaftInstallationDiscovery.RmlRootPathKey]);
    }

    [Fact]
    public void WorldDiscoveryFindsOnlySameNameCurrentWorldFile()
    {
        var userRoot = Path.Combine(_root, "worlds");
        var profile = Path.Combine(userRoot, "User_76561198000000000");
        var world = Path.Combine(profile, "World", "OceanHome");
        Directory.CreateDirectory(world);
        File.WriteAllBytes(Path.Combine(world, "OceanHome.rgd"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(world, "2026-01-01.rgd"), [4]);
        Directory.CreateDirectory(Path.Combine(profile, "Player"));
        File.WriteAllBytes(
            Path.Combine(profile, "Player", "RGD_Users.rgd"),
            [5]);
        var invalid = Path.Combine(
            userRoot,
            "User_not-a-steamid",
            "World",
            "Wrong");
        Directory.CreateDirectory(invalid);
        File.WriteAllBytes(Path.Combine(invalid, "Wrong.rgd"), [6]);
        var mismatch = Path.Combine(profile, "World", "Mismatch");
        Directory.CreateDirectory(mismatch);
        File.WriteAllBytes(Path.Combine(mismatch, "Other.rgd"), [7]);

        var detected = Assert.Single(
            RaftWorldDiscovery.DiscoverFromUserRoot(userRoot));

        Assert.Equal("OceanHome", detected.DisplayName);
        Assert.Equal("local:User_76561198000000000:OceanHome", detected.Id);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(world, "OceanHome.rgd")),
            detected.SourcePath);
    }

    [Fact]
    public void WorldDiscoveryRejectsLinkedCurrentWorldFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userRoot = Path.Combine(_root, "linked");
        var world = Path.Combine(
            userRoot,
            "User_76561198000000000",
            "World",
            "Linked");
        Directory.CreateDirectory(world);
        var outside = Path.Combine(_root, "outside.rgd");
        File.WriteAllBytes(outside, [1, 2, 3]);
        var link = Path.Combine(world, "Linked.rgd");
        File.CreateSymbolicLink(link, outside);
        try
        {
            Assert.Empty(RaftWorldDiscovery.DiscoverFromUserRoot(userRoot));
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
        var environment = RaftEnvironment.Inspect(installation);

        Assert.Equal(1, environment.SchemaVersion);
        Assert.Equal("raft", environment.AdapterId);
        Assert.Equal("24680", environment.GameVersion);
        Assert.Empty(environment.Components);
    }

    [Fact]
    public void EnvironmentInspectionRejectsGameRootMods()
    {
        var installation = CreateInstallation("24680");
        var mods = installation.Metadata![RaftInstallationDiscovery.ModsRootPathKey];
        Directory.CreateDirectory(mods);
        File.WriteAllBytes(Path.Combine(mods, "Example.rmod"), [1]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RaftEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentInspectionRejectsRaftModLoaderInstallation()
    {
        var installation = CreateInstallation("24680");
        var rml = installation.Metadata![RaftInstallationDiscovery.RmlRootPathKey];
        Directory.CreateDirectory(rml);
        File.WriteAllBytes(Path.Combine(rml, "HMLCore.exe"), [1]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RaftEnvironment.Inspect(installation));

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
        var mods = installation.Metadata![RaftInstallationDiscovery.ModsRootPathKey];
        var outside = Path.Combine(_root, "outside-mods");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(mods, outside);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                RaftEnvironment.Inspect(installation));
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
        var manifest = installation.Metadata![RaftInstallationDiscovery.SteamManifestPathKey];
        using (var stream = new FileStream(
                   manifest,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(RaftEnvironment.MaximumSteamManifestBytes + 1);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RaftEnvironment.Inspect(installation));

        Assert.Contains("metadata safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRestoreCapturePreservesOnlyCurrentWorldBytes()
    {
        var installation = CreateInstallation("13579");
        var environment = RaftEnvironment.Inspect(installation);
        var userRoot = installation.Metadata![RaftInstallationDiscovery.UserRootPathKey];
        var profile = Path.Combine(userRoot, "User_76561198000000000");
        var worldDir = Path.Combine(profile, "World", "OceanHome");
        Directory.CreateDirectory(worldDir);
        var source = Path.Combine(worldDir, "OceanHome.rgd");
        var bytes = Enumerable.Range(0, 16384)
            .Select(index => (byte)((index * 13) % 251))
            .ToArray();
        File.WriteAllBytes(source, bytes);
        File.WriteAllBytes(Path.Combine(worldDir, "backup.rgd"), [9, 9, 9]);
        Directory.CreateDirectory(Path.Combine(profile, "Player"));
        File.WriteAllBytes(
            Path.Combine(profile, "Player", "RGD_Users.rgd"),
            [8, 8, 8]);
        var detected = Assert.Single(
            RaftWorldDiscovery.DiscoverFromUserRoot(userRoot));
        var adapter = new RaftAdapter();

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
                File.ReadAllBytes(Path.Combine(workspace, "save", "world.rgd")));

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
        var environment = RaftEnvironment.Inspect(installation);
        var empty = Path.Combine(_root, "empty.rgd");
        File.WriteAllBytes(empty, []);
        var adapter = new RaftAdapter();
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
            "raft",
            "22222",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var adapter = new RaftAdapter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.PrepareEnvironmentAsync(installation, required));

        Assert.Contains(
            "does not match required build",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileIdentityRequiresCanonicalUserSteamId()
    {
        Assert.True(RaftWorldDiscovery.TryGetSteamId(
            "User_76561198000000000",
            out var steamId));
        Assert.Equal(76561198000000000UL, steamId);
        Assert.False(RaftWorldDiscovery.TryGetSteamId(
            "User_076561198000000000",
            out _));
        Assert.False(RaftWorldDiscovery.TryGetSteamId(
            "76561198000000000",
            out _));
    }

    [Fact]
    public void AdapterAdvertisesOnlyExactGameVersion()
    {
        Assert.Equal(
            GameAdapterCapabilities.ExactGameVersion,
            new RaftAdapter().Capabilities);
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var library = Path.Combine(_root, $"library-{Guid.NewGuid():N}");
        var installRoot = Path.Combine(library, "steamapps", "common", "Raft");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Raft.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_648800.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\" {{ \"appid\" \"648800\" \"buildid\" \"{buildId}\" }}");
        var userRoot = Path.Combine(_root, $"user-root-{Guid.NewGuid():N}");
        var rmlRoot = Path.Combine(_root, $"rml-{Guid.NewGuid():N}");

        return new GameInstallation(
            $"raft:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [RaftInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(
                    installRoot,
                    "Raft.exe"),
                [RaftInstallationDiscovery.SteamManifestPathKey] = manifest,
                [RaftInstallationDiscovery.UserRootPathKey] = userRoot,
                [RaftInstallationDiscovery.ModsRootPathKey] = Path.Combine(
                    installRoot,
                    "mods"),
                [RaftInstallationDiscovery.RmlRootPathKey] = rmlRoot,
                [RaftInstallationDiscovery.GameSteamAppIdKey] =
                    RaftInstallationDiscovery.GameSteamAppId
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
