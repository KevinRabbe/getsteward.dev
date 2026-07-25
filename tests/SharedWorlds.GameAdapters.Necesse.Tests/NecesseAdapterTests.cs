using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.Necesse.Tests;

public sealed class NecesseAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-necesse-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];

    [Fact]
    public void InstallationDiscoveryFindsSteamInstallAndDataRoots()
    {
        var library = Path.Combine(_root, "steam-library");
        var installRoot = Path.Combine(library, "steamapps", "common", "Necesse");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Necesse.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_1169040.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "\"AppState\" { \"buildid\" \"12345\" }");
        var worldRoot = Path.Combine(_root, "AppData", "Necesse", "saves", "worlds");
        var modsRoot = Path.Combine(_root, "AppData", "Necesse", "mods");

        var installation = Assert.Single(
            NecesseInstallationDiscovery.DiscoverFromSteamLibraries([library], worldRoot, modsRoot));

        Assert.Equal(Path.GetFullPath(installRoot), installation.RootPath);
        Assert.Equal("steam", installation.Source);
        Assert.NotNull(installation.Metadata);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "Necesse.exe")),
            installation.Metadata[NecesseInstallationDiscovery.ClientExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(manifest),
            installation.Metadata[NecesseInstallationDiscovery.SteamManifestPathKey]);
        Assert.Equal(
            Path.GetFullPath(worldRoot),
            installation.Metadata[NecesseInstallationDiscovery.WorldRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(modsRoot),
            installation.Metadata[NecesseInstallationDiscovery.ModsRootPathKey]);
        Assert.Equal("1169040", installation.Metadata[NecesseInstallationDiscovery.GameSteamAppIdKey]);
    }

    [Fact]
    public void WorldDiscoveryIncludesOnlyCompressedWorldFiles()
    {
        var worldRoot = Path.Combine(_root, "worlds");
        Directory.CreateDirectory(worldRoot);
        CreateWorldZip(Path.Combine(worldRoot, "Colony.zip"), "Colony");
        Directory.CreateDirectory(Path.Combine(worldRoot, "UncompressedWorld"));
        File.WriteAllText(Path.Combine(worldRoot, "notes.txt"), "not a world");

        var world = Assert.Single(NecesseWorldDiscovery.DiscoverFromWorldRoot(worldRoot));

        Assert.Equal("local:Colony", world.Id);
        Assert.Equal("Colony", world.DisplayName);
        Assert.Equal(Path.GetFullPath(Path.Combine(worldRoot, "Colony.zip")), world.SourcePath);
    }

    [Fact]
    public void WorldDiscoverySkipsLinkedZip()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var worldRoot = Path.Combine(_root, "linked-worlds");
        Directory.CreateDirectory(worldRoot);
        var outside = Path.Combine(_root, "outside.zip");
        CreateWorldZip(outside, "Outside");
        var linked = Path.Combine(worldRoot, "Linked.zip");
        File.CreateSymbolicLink(linked, outside);
        try
        {
            Assert.Empty(NecesseWorldDiscovery.DiscoverFromWorldRoot(worldRoot));
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

        var environment = NecesseEnvironment.Inspect(installation);

        Assert.Equal(1, environment.SchemaVersion);
        Assert.Equal("necesse", environment.AdapterId);
        Assert.Equal("24680", environment.GameVersion);
        Assert.Empty(environment.Components);
        Assert.Empty(environment.Configuration);
    }

    [Fact]
    public void EnvironmentInspectionRejectsNonEmptyModsDirectory()
    {
        var installation = CreateInstallation("24680");
        var modsRoot = installation.Metadata![NecesseInstallationDiscovery.ModsRootPathKey];
        Directory.CreateDirectory(modsRoot);
        File.WriteAllBytes(Path.Combine(modsRoot, "example.jar"), [1, 2, 3]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            NecesseEnvironment.Inspect(installation));

        Assert.Contains("vanilla-only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedSteamManifestFailsBeforeParsing()
    {
        var installation = CreateInstallation("12345");
        var manifest = installation.Metadata![NecesseInstallationDiscovery.SteamManifestPathKey];
        using (var stream = new FileStream(manifest, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(NecesseEnvironment.MaximumSteamManifestBytes + 1);
        }

        var exception = Assert.Throws<InvalidOperationException>(() =>
            NecesseEnvironment.Inspect(installation));

        Assert.Contains("metadata safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRestoreCaptureRoundTripPreservesWorldZipBytes()
    {
        var installation = CreateInstallation("13579");
        var environment = NecesseEnvironment.Inspect(installation);
        var source = Path.Combine(_root, "RoundTrip.zip");
        CreateWorldZip(source, "RoundTrip");
        var expected = File.ReadAllBytes(source);
        var adapter = new NecesseAdapter();
        var detected = new DetectedWorld("local:RoundTrip", "RoundTrip", source);

        var imported = await adapter.CaptureDetectedWorldAsync(installation, detected);
        _packages.Add(imported.Package.Path);
        Assert.Equal(expected, File.ReadAllBytes(imported.Package.Path));
        Assert.Equal(expected, File.ReadAllBytes(source));

        var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);
        var workspace = prepared.WorkingDirectory;
        try
        {
            await adapter.RestoreStateAsync(prepared, imported.Package);
            var restored = Path.Combine(workspace, "saves", "worlds", "world.zip");
            Assert.Equal(expected, File.ReadAllBytes(restored));

            var recaptured = await adapter.CaptureStateAsync(prepared);
            _packages.Add(recaptured.Package.Path);
            Assert.Equal(expected, File.ReadAllBytes(recaptured.Package.Path));

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
            "necesse",
            "22222",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var adapter = new NecesseAdapter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.PrepareEnvironmentAsync(installation, required));

        Assert.Contains("does not match required build", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterAdvertisesOnlyExactGameVersion()
    {
        var adapter = new NecesseAdapter();

        Assert.Equal(GameAdapterCapabilities.ExactGameVersion, adapter.Capabilities);
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var installRoot = Path.Combine(_root, $"install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Necesse.exe"), [1]);
        var manifest = Path.Combine(_root, $"appmanifest-{Guid.NewGuid():N}.acf");
        File.WriteAllText(manifest, $"\"AppState\" {{ \"buildid\" \"{buildId}\" }}");
        var dataRoot = Path.Combine(_root, $"data-{Guid.NewGuid():N}");
        return new GameInstallation(
            $"necesse:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NecesseInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(installRoot, "Necesse.exe"),
                [NecesseInstallationDiscovery.SteamManifestPathKey] = manifest,
                [NecesseInstallationDiscovery.WorldRootPathKey] = Path.Combine(dataRoot, "saves", "worlds"),
                [NecesseInstallationDiscovery.ModsRootPathKey] = Path.Combine(dataRoot, "mods"),
                [NecesseInstallationDiscovery.GameSteamAppIdKey] = NecesseInstallationDiscovery.GameSteamAppId
            });
    }

    private static void CreateWorldZip(string path, string worldName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var settings = archive.CreateEntry($"{worldName}/worldSettings.cfg");
        using var writer = new StreamWriter(settings.Open());
        writer.Write("difficulty = CLASSIC");
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
