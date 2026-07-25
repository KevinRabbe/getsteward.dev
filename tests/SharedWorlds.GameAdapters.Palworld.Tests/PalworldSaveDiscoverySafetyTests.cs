using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Palworld.Tests;

public sealed class PalworldSaveDiscoverySafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-palworld-discovery-safety-{Guid.NewGuid():N}");

    [Fact]
    public void DiscoverIncludesRegularDedicatedWorld()
    {
        var serverRoot = CreateServerRoot();
        var worldPath = CreateWorld(serverRoot, "0", "regular-world");

        var worlds = PalworldSaveDiscovery.Discover(CreateInstallation(serverRoot));

        Assert.Contains(worlds, world =>
            string.Equals(
                world.SourcePath,
                Path.GetFullPath(worldPath),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal));
    }

    [Fact]
    public void DiscoverSkipsLinkedDedicatedServerRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var realServerRoot = CreateServerRoot("real-server");
        _ = CreateWorld(realServerRoot, "0", "outside-world");
        var linkedServerRoot = Path.Combine(_root, "linked-server");
        Directory.CreateSymbolicLink(linkedServerRoot, realServerRoot);
        try
        {
            var worlds = PalworldSaveDiscovery.Discover(CreateInstallation(linkedServerRoot));

            Assert.DoesNotContain(worlds, world =>
                world.Id.Contains("outside-world", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(linkedServerRoot);
        }
    }

    [Fact]
    public void DiscoverSkipsLinkedProfileDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var serverRoot = CreateServerRoot();
        var saveGamesRoot = GetSaveGamesRoot(serverRoot);
        var outsideProfile = Path.Combine(_root, "outside-profile");
        _ = CreateWorldUnderProfile(outsideProfile, "linked-profile-world");
        var linkedProfile = Path.Combine(saveGamesRoot, "linked-profile");
        Directory.CreateSymbolicLink(linkedProfile, outsideProfile);
        try
        {
            var worlds = PalworldSaveDiscovery.Discover(CreateInstallation(serverRoot));

            Assert.DoesNotContain(worlds, world =>
                world.Id.Contains("linked-profile-world", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(linkedProfile);
        }
    }

    [Fact]
    public void DiscoverSkipsLinkedWorldDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var serverRoot = CreateServerRoot();
        var profile = Path.Combine(GetSaveGamesRoot(serverRoot), "0");
        Directory.CreateDirectory(profile);
        var outsideWorld = Path.Combine(_root, "outside-world-root");
        Directory.CreateDirectory(outsideWorld);
        File.WriteAllBytes(Path.Combine(outsideWorld, "Level.sav"), [1, 2, 3]);
        var linkedWorld = Path.Combine(profile, "linked-world");
        Directory.CreateSymbolicLink(linkedWorld, outsideWorld);
        try
        {
            var worlds = PalworldSaveDiscovery.Discover(CreateInstallation(serverRoot));

            Assert.DoesNotContain(worlds, world =>
                string.Equals(
                    world.SourcePath,
                    Path.GetFullPath(linkedWorld),
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(linkedWorld);
        }
    }

    [Fact]
    public void DiscoverSkipsWorldWhoseLevelSaveIsLinked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var serverRoot = CreateServerRoot();
        var world = Path.Combine(GetSaveGamesRoot(serverRoot), "0", "linked-level");
        Directory.CreateDirectory(world);
        var outsideLevel = Path.Combine(_root, "outside-level.sav");
        File.WriteAllBytes(outsideLevel, [7, 8, 9]);
        var linkedLevel = Path.Combine(world, "Level.sav");
        File.CreateSymbolicLink(linkedLevel, outsideLevel);
        try
        {
            var worlds = PalworldSaveDiscovery.Discover(CreateInstallation(serverRoot));

            Assert.DoesNotContain(worlds, candidate =>
                string.Equals(
                    candidate.SourcePath,
                    Path.GetFullPath(world),
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(linkedLevel);
        }
    }

    private string CreateServerRoot(string name = "server")
    {
        var serverRoot = Path.Combine(_root, name);
        Directory.CreateDirectory(GetSaveGamesRoot(serverRoot));
        return serverRoot;
    }

    private static string GetSaveGamesRoot(string serverRoot)
        => Path.Combine(serverRoot, "Pal", "Saved", "SaveGames");

    private static string CreateWorld(
        string serverRoot,
        string profileId,
        string worldId)
    {
        var profile = Path.Combine(GetSaveGamesRoot(serverRoot), profileId);
        Directory.CreateDirectory(profile);
        return CreateWorldUnderProfile(profile, worldId);
    }

    private static string CreateWorldUnderProfile(string profilePath, string worldId)
    {
        var world = Path.Combine(profilePath, worldId);
        Directory.CreateDirectory(world);
        File.WriteAllBytes(Path.Combine(world, "Level.sav"), [1, 2, 3]);
        return world;
    }

    private static GameInstallation CreateInstallation(string serverRoot)
        => new(
            Id: $"palworld:test:{serverRoot}",
            RootPath: serverRoot,
            Source: "test",
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PalworldInstallationDiscovery.DedicatedServerRootPathKey] = serverRoot
            });

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
