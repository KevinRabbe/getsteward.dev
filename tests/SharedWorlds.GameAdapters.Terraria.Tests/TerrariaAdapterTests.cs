using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using Xunit;

namespace SharedWorlds.GameAdapters.Terraria.Tests;

public sealed class TerrariaAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-terraria-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];

    [Fact]
    public void InstallationDiscoveryFindsSteamInstallAndManifest()
    {
        var library = Path.Combine(_root, "steam-library");
        var installRoot = Path.Combine(library, "steamapps", "common", "Terraria");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Terraria.exe"), [1]);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_105600.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, "\"AppState\" { \"buildid\" \"12345\" }");
        var worldRoot = Path.Combine(_root, "Documents", "My Games", "Terraria", "Worlds");

        var installation = Assert.Single(
            TerrariaInstallationDiscovery.DiscoverFromSteamLibraries([library], worldRoot));

        Assert.Equal("steam", installation.Source);
        Assert.Equal(Path.GetFullPath(installRoot), installation.RootPath);
        Assert.NotNull(installation.Metadata);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(installRoot, "Terraria.exe")),
            installation.Metadata[TerrariaInstallationDiscovery.ClientExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(manifest),
            installation.Metadata[TerrariaInstallationDiscovery.SteamManifestPathKey]);
        Assert.Equal(
            Path.GetFullPath(worldRoot),
            installation.Metadata[TerrariaInstallationDiscovery.UserDataPathKey]);
        Assert.Equal("105600", installation.Metadata[TerrariaInstallationDiscovery.GameSteamAppIdKey]);
    }

    [Fact]
    public void WorldDiscoveryIncludesOnlyCurrentLocalWorldFiles()
    {
        var worldRoot = Path.Combine(_root, "worlds");
        Directory.CreateDirectory(worldRoot);
        File.WriteAllBytes(Path.Combine(worldRoot, "Alpha.wld"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(worldRoot, "Alpha.wld.bak"), [9, 9, 9]);
        File.WriteAllText(Path.Combine(worldRoot, "notes.txt"), "not a world");

        var world = Assert.Single(TerrariaWorldDiscovery.DiscoverFromWorldRoot(worldRoot));

        Assert.Equal("local:Alpha", world.Id);
        Assert.Equal("Alpha", world.DisplayName);
        Assert.Equal(Path.GetFullPath(Path.Combine(worldRoot, "Alpha.wld")), world.SourcePath);
    }

    [Fact]
    public void WorldDiscoverySkipsLinkedWorldFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var worldRoot = Path.Combine(_root, "linked-worlds");
        Directory.CreateDirectory(worldRoot);
        var outside = Path.Combine(_root, "outside.wld");
        File.WriteAllBytes(outside, [4, 5, 6]);
        var linked = Path.Combine(worldRoot, "Linked.wld");
        File.CreateSymbolicLink(linked, outside);
        try
        {
            Assert.Empty(TerrariaWorldDiscovery.DiscoverFromWorldRoot(worldRoot));
        }
        finally
        {
            File.Delete(linked);
        }
    }

    [Fact]
    public void EnvironmentInspectionUsesExactSteamBuildId()
    {
        var installation = CreateInstallation("24680");

        var environment = TerrariaEnvironment.Inspect(installation);

        Assert.Equal(1, environment.SchemaVersion);
        Assert.Equal("terraria", environment.AdapterId);
        Assert.Equal("24680", environment.GameVersion);
        Assert.Empty(environment.Components);
        Assert.Empty(environment.Configuration);
    }

    [Fact]
    public void OversizedSteamManifestFailsBeforeParsing()
    {
        var installation = CreateInstallation("12345");
        var manifest = installation.Metadata![TerrariaInstallationDiscovery.SteamManifestPathKey];
        using (var stream = new FileStream(manifest, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(TerrariaEnvironment.MaximumSteamManifestBytes + 1);
        }

        var exception = Assert.Throws<InvalidOperationException>(() => TerrariaEnvironment.Inspect(installation));

        Assert.Contains("metadata safety limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureRestoreCaptureRoundTripPreservesWorldBytes()
    {
        var installation = CreateInstallation("13579");
        var environment = TerrariaEnvironment.Inspect(installation);
        var source = Path.Combine(_root, "RoundTrip.wld");
        var expected = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        File.WriteAllBytes(source, expected);
        var original = File.ReadAllBytes(source);
        var adapter = new TerrariaAdapter();
        var detected = new DetectedWorld("local:RoundTrip", "RoundTrip", source);

        var imported = await adapter.CaptureDetectedWorldAsync(installation, detected);
        _packages.Add(imported.Package.Path);
        var prepared = await adapter.PrepareEnvironmentAsync(installation, environment);
        var workspace = prepared.WorkingDirectory;
        try
        {
            await adapter.RestoreStateAsync(prepared, imported.Package);
            var recaptured = await adapter.CaptureStateAsync(prepared);
            _packages.Add(recaptured.Package.Path);

            Assert.Equal(expected, File.ReadAllBytes(recaptured.Package.Path));
            Assert.Equal(original, File.ReadAllBytes(source));

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
            "terraria",
            "22222",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        var adapter = new TerrariaAdapter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.PrepareEnvironmentAsync(installation, required));

        Assert.Contains("does not match required build", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterAdvertisesOnlyExactGameVersion()
    {
        var adapter = new TerrariaAdapter();

        Assert.Equal(GameAdapterCapabilities.ExactGameVersion, adapter.Capabilities);
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var installRoot = Path.Combine(_root, $"install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(installRoot);
        File.WriteAllBytes(Path.Combine(installRoot, "Terraria.exe"), [1]);
        var manifest = Path.Combine(_root, $"appmanifest-{Guid.NewGuid():N}.acf");
        File.WriteAllText(manifest, $"\"AppState\" {{ \"buildid\" \"{buildId}\" }}");
        var worldRoot = Path.Combine(_root, "worlds");

        return new GameInstallation(
            $"terraria:test:{Guid.NewGuid():N}",
            installRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TerrariaInstallationDiscovery.ClientExecutablePathKey] = Path.Combine(installRoot, "Terraria.exe"),
                [TerrariaInstallationDiscovery.SteamManifestPathKey] = manifest,
                [TerrariaInstallationDiscovery.UserDataPathKey] = worldRoot,
                [TerrariaInstallationDiscovery.GameSteamAppIdKey] = TerrariaInstallationDiscovery.GameSteamAppId
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
