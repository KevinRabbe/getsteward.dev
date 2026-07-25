using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.ProjectZomboid.Tests;

public sealed class ProjectZomboidDiscoverySafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-pz-discovery-safety-{Guid.NewGuid():N}");

    [Fact]
    public void DiscoverSkipsLinkedUserDataRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var realUserData = Path.Combine(_root, "real-user-data");
        CreateWorld(realUserData, "linked-root-world");
        var linkedUserData = Path.Combine(_root, "linked-user-data");
        Directory.CreateSymbolicLink(linkedUserData, realUserData);
        try
        {
            var worlds = ProjectZomboidWorldDiscovery.Discover(CreateInstallation(linkedUserData));

            Assert.Empty(worlds);
        }
        finally
        {
            Directory.Delete(linkedUserData);
        }
    }

    [Fact]
    public void DiscoverSkipsLinkedWorldDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = Path.Combine(_root, "user-data-world-link");
        var multiplayerRoot = Path.Combine(userData, "Saves", "Multiplayer");
        var serverRoot = Path.Combine(userData, "Server");
        Directory.CreateDirectory(multiplayerRoot);
        Directory.CreateDirectory(serverRoot);
        var outsideWorld = Path.Combine(_root, "outside-pz-world");
        Directory.CreateDirectory(outsideWorld);
        File.WriteAllBytes(Path.Combine(outsideWorld, "map_t.bin"), [1, 2, 3]);
        var linkedWorld = Path.Combine(multiplayerRoot, "linked-world");
        Directory.CreateSymbolicLink(linkedWorld, outsideWorld);
        File.WriteAllText(Path.Combine(serverRoot, "linked-world.ini"), "PublicName=Linked");
        try
        {
            var worlds = ProjectZomboidWorldDiscovery.Discover(CreateInstallation(userData));

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
    public void DiscoverSkipsWorldWhoseMapTimeFileIsLinked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = Path.Combine(_root, "user-data-map-link");
        var world = Path.Combine(userData, "Saves", "Multiplayer", "linked-map");
        var serverRoot = Path.Combine(userData, "Server");
        Directory.CreateDirectory(world);
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(Path.Combine(serverRoot, "linked-map.ini"), "PublicName=Linked map");
        var outsideMap = Path.Combine(_root, "outside-map_t.bin");
        File.WriteAllBytes(outsideMap, [4, 5, 6]);
        var linkedMap = Path.Combine(world, "map_t.bin");
        File.CreateSymbolicLink(linkedMap, outsideMap);
        try
        {
            var worlds = ProjectZomboidWorldDiscovery.Discover(CreateInstallation(userData));

            Assert.DoesNotContain(worlds, candidate =>
                string.Equals(
                    candidate.SourcePath,
                    Path.GetFullPath(world),
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(linkedMap);
        }
    }

    [Fact]
    public void DiscoverSkipsWorldWhoseServerConfigIsLinked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = Path.Combine(_root, "user-data-config-link");
        var world = Path.Combine(userData, "Saves", "Multiplayer", "linked-config");
        var serverRoot = Path.Combine(userData, "Server");
        Directory.CreateDirectory(world);
        Directory.CreateDirectory(serverRoot);
        File.WriteAllBytes(Path.Combine(world, "map_t.bin"), [7, 8, 9]);
        var outsideConfig = Path.Combine(_root, "outside-server.ini");
        File.WriteAllText(outsideConfig, "PublicName=Outside");
        var linkedConfig = Path.Combine(serverRoot, "linked-config.ini");
        File.CreateSymbolicLink(linkedConfig, outsideConfig);
        try
        {
            var worlds = ProjectZomboidWorldDiscovery.Discover(CreateInstallation(userData));

            Assert.DoesNotContain(worlds, candidate =>
                string.Equals(
                    candidate.SourcePath,
                    Path.GetFullPath(world),
                    StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(linkedConfig);
        }
    }

    private static void CreateWorld(string userData, string serverName)
    {
        var world = Path.Combine(userData, "Saves", "Multiplayer", serverName);
        var serverRoot = Path.Combine(userData, "Server");
        Directory.CreateDirectory(world);
        Directory.CreateDirectory(serverRoot);
        File.WriteAllBytes(Path.Combine(world, "map_t.bin"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(serverRoot, serverName + ".ini"), "PublicName=Steward");
    }

    private static GameInstallation CreateInstallation(string userData)
        => new(
            "project-zomboid:test",
            userData,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProjectZomboidInstallationDiscovery.UserDataPathKey] = userData
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
