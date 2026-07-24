using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.ProjectZomboid.Tests;

public sealed class ProjectZomboidDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-pz-discovery-{Guid.NewGuid():N}");

    [Fact]
    public void SteamDiscoveryPairsClientWithDedicatedServerAcrossLibraries()
    {
        var clientLibrary = Path.Combine(_root, "client-library");
        var serverLibrary = Path.Combine(_root, "server-library");
        var clientRoot = Path.Combine(clientLibrary, "steamapps", "common", "Custom PZ Client");
        var serverRoot = Path.Combine(serverLibrary, "steamapps", "common", "Custom PZ Server");
        Directory.CreateDirectory(clientRoot);
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(Path.Combine(clientRoot, "ProjectZomboid64.exe"), string.Empty);
        File.WriteAllText(Path.Combine(serverRoot, "StartServer64.bat"), string.Empty);
        Directory.CreateDirectory(Path.Combine(clientLibrary, "steamapps"));
        Directory.CreateDirectory(Path.Combine(serverLibrary, "steamapps"));
        File.WriteAllText(
            Path.Combine(clientLibrary, "steamapps", "appmanifest_108600.acf"),
            "\"AppState\"\n{\n    \"installdir\"    \"Custom PZ Client\"\n}");
        var serverManifest = Path.Combine(serverLibrary, "steamapps", "appmanifest_380870.acf");
        File.WriteAllText(
            serverManifest,
            "\"AppState\"\n{\n    \"installdir\"    \"Custom PZ Server\"\n}");

        var userData = Path.Combine(_root, "Zomboid");
        var installations = ProjectZomboidInstallationDiscovery.DiscoverFromSteamLibraries(
            [clientLibrary, serverLibrary],
            userData);

        var installation = Assert.Single(installations);
        Assert.Equal(Path.GetFullPath(clientRoot), installation.RootPath);
        Assert.Equal("steam", installation.Source);
        Assert.NotNull(installation.Metadata);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(clientRoot, "ProjectZomboid64.exe")),
            installation.Metadata[ProjectZomboidInstallationDiscovery.ClientExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(serverRoot),
            installation.Metadata[ProjectZomboidInstallationDiscovery.DedicatedServerRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(serverRoot, "StartServer64.bat")),
            installation.Metadata[ProjectZomboidInstallationDiscovery.DedicatedServerLaunchPathKey]);
        Assert.Equal(
            Path.GetFullPath(serverManifest),
            installation.Metadata[ProjectZomboidInstallationDiscovery.DedicatedServerManifestPathKey]);
        Assert.Equal("installed", installation.Metadata[ProjectZomboidInstallationDiscovery.DedicatedServerInstallStateKey]);
        Assert.Equal(Path.GetFullPath(userData), installation.Metadata[ProjectZomboidInstallationDiscovery.UserDataPathKey]);
    }

    [Fact]
    public void SteamDiscoveryKeepsClientUsableWhenDedicatedServerIsMissing()
    {
        var library = Path.Combine(_root, "library");
        var clientRoot = Path.Combine(library, "steamapps", "common", "ProjectZomboid");
        Directory.CreateDirectory(clientRoot);
        File.WriteAllText(Path.Combine(clientRoot, "ProjectZomboid64.exe"), string.Empty);

        var installations = ProjectZomboidInstallationDiscovery.DiscoverFromSteamLibraries(
            [library],
            Path.Combine(_root, "Zomboid"));

        var installation = Assert.Single(installations);
        Assert.Equal(
            "not-installed-in-discovered-steam-libraries",
            installation.Metadata![ProjectZomboidInstallationDiscovery.DedicatedServerInstallStateKey]);
        Assert.False(installation.Metadata.ContainsKey(ProjectZomboidInstallationDiscovery.DedicatedServerLaunchPathKey));
    }

    [Fact]
    public void WorldDiscoveryOnlyReturnsServerWorldFoldersWithMapTimeState()
    {
        var userData = Path.Combine(_root, "Zomboid");
        var multiplayer = Path.Combine(userData, "Saves", "Multiplayer");
        var serverWorld = Path.Combine(multiplayer, "servertest");
        var playerCache = Path.Combine(multiplayer, "servertest_player");
        var remoteCache = Path.Combine(multiplayer, "192.168.1.5_16261_hash");
        Directory.CreateDirectory(serverWorld);
        Directory.CreateDirectory(playerCache);
        Directory.CreateDirectory(remoteCache);
        File.WriteAllBytes(Path.Combine(serverWorld, "map_t.bin"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(serverWorld, "players.db"), [4, 5]);
        File.WriteAllBytes(Path.Combine(playerCache, "players.db"), [6]);

        var installation = new GameInstallation(
            "project-zomboid:test",
            _root,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProjectZomboidInstallationDiscovery.UserDataPathKey] = userData
            });

        var worlds = ProjectZomboidWorldDiscovery.Discover(installation);

        var world = Assert.Single(worlds);
        Assert.Equal("servertest", world.DisplayName);
        Assert.Equal(Path.GetFullPath(serverWorld), world.Id);
        Assert.Equal(Path.GetFullPath(serverWorld), world.SourcePath);
    }

    [Fact]
    public void WorldDiscoveryReturnsEmptyWhenUserDataMetadataIsMissing()
    {
        var installation = new GameInstallation("project-zomboid:test", _root, "test");

        Assert.Empty(ProjectZomboidWorldDiscovery.Discover(installation));
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
