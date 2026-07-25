using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Palworld.Tests;

public sealed class PalworldDedicatedManifestInputSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-palworld-manifest-input-safety-{Guid.NewGuid():N}");

    [Fact]
    public async Task InspectEnvironmentRejectsOversizedDedicatedServerManifest()
    {
        var manifestPath = Path.Combine(_root, "steamapps", "appmanifest_2394010.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await using (var stream = new FileStream(
                         manifestPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None))
        {
            stream.SetLength(PalworldDedicatedRuntimeInputSafety.MaximumDedicatedServerManifestBytes + 1L);
        }

        var installation = new GameInstallation(
            "palworld:test",
            _root,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PalworldInstallationDiscovery.DedicatedServerManifestPathKey] = manifestPath
            });
        var worldPath = Path.Combine(_root, "A1B2C3D4");
        var adapter = new PalworldAdapter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.InspectEnvironmentAsync(
                installation,
                new DetectedWorld("palworld:test", "World", worldPath)));

        Assert.Contains("Steam manifest", exception.Message, StringComparison.Ordinal);
        Assert.Contains("safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            PalworldDedicatedRuntimeInputSafety.MaximumDedicatedServerManifestBytes + 1L,
            new FileInfo(manifestPath).Length);
    }

    [Fact]
    public async Task DedicatedHostingReadOwnerRejectsOversizedManifestWithoutAdapterPreflight()
    {
        var manifestPath = Path.Combine(_root, "steamapps", "appmanifest_2394010.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await using (var stream = new FileStream(
                         manifestPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None))
        {
            stream.SetLength(PalworldDedicatedRuntimeInputSafety.MaximumDedicatedServerManifestBytes + 1L);
        }

        var installation = new GameInstallation(
            "palworld:test",
            _root,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PalworldInstallationDiscovery.DedicatedServerManifestPathKey] = manifestPath
            });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PalworldDedicatedServerHosting.InspectEnvironment(
                installation,
                new DetectedWorld(
                    "palworld:test",
                    "World",
                    Path.Combine(_root, "A1B2C3D4"))));

        Assert.Contains("Steam manifest", exception.Message, StringComparison.Ordinal);
        Assert.Contains("safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            PalworldDedicatedRuntimeInputSafety.MaximumDedicatedServerManifestBytes + 1L,
            new FileInfo(manifestPath).Length);
    }

    [Fact]
    public async Task MissingDedicatedServerManifestStillReportsUnknownBuild()
    {
        var missingManifestPath = Path.Combine(_root, "steamapps", "missing-appmanifest_2394010.acf");
        var installation = new GameInstallation(
            "palworld:test",
            _root,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PalworldInstallationDiscovery.DedicatedServerManifestPathKey] = missingManifestPath
            });
        var adapter = new PalworldAdapter();

        var environment = await adapter.InspectEnvironmentAsync(
            installation,
            new DetectedWorld("palworld:test", "World", Path.Combine(_root, "A1B2C3D4")));

        Assert.Equal("unknown", environment.GameVersion);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
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
