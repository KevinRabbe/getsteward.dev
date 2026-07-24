using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

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
            "\"AppState\"\n{\n    \"installdir\"    \"Custom PZ Server\"\n    \"buildid\"    \"87654321\"\n}");

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
    public void WorldDiscoveryRequiresMatchingLocalServerDefinition()
    {
        var userData = Path.Combine(_root, "Zomboid");
        var multiplayer = Path.Combine(userData, "Saves", "Multiplayer");
        var serverConfig = Path.Combine(userData, "Server");
        var serverWorld = Path.Combine(multiplayer, "servertest");
        var playerCache = Path.Combine(multiplayer, "servertest_player");
        var remoteCache = Path.Combine(multiplayer, "192.168.1.5_16261_hash");
        Directory.CreateDirectory(serverWorld);
        Directory.CreateDirectory(playerCache);
        Directory.CreateDirectory(remoteCache);
        Directory.CreateDirectory(serverConfig);
        File.WriteAllBytes(Path.Combine(serverWorld, "map_t.bin"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(serverWorld, "players.db"), [4, 5]);
        File.WriteAllBytes(Path.Combine(playerCache, "players.db"), [6]);
        File.WriteAllBytes(Path.Combine(remoteCache, "map_t.bin"), [7, 8]);
        File.WriteAllText(Path.Combine(serverConfig, "servertest.ini"), "PublicName=Steward Test");

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

    [Fact]
    public void EnvironmentInspectionUsesDedicatedServerSteamBuildId()
    {
        var installation = CreateInstallationWithDedicatedServerBuild("87654321");
        var world = CreateEnvironmentWorld(installation);

        var environment = ProjectZomboidEnvironment.Inspect(installation, world);

        Assert.Equal(1, environment.SchemaVersion);
        Assert.Equal("project-zomboid", environment.AdapterId);
        Assert.Equal("87654321", environment.GameVersion);
        Assert.Empty(environment.Components);
        Assert.Empty(environment.Configuration);
    }

    [Fact]
    public void EnvironmentInspectionFailsClosedWithoutDedicatedServerManifest()
    {
        var installation = new GameInstallation(
            "project-zomboid:test",
            _root,
            "steam",
            new Dictionary<string, string>(StringComparer.Ordinal));
        var world = new DetectedWorld(_root, "servertest", _root);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectZomboidEnvironment.Inspect(installation, world));

        Assert.Contains("Dedicated Server", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentVerificationFailsClosedOnDedicatedServerBuildMismatch()
    {
        var installation = CreateInstallationWithDedicatedServerBuild("22222222");
        var required = new EnvironmentManifest(
            1,
            "project-zomboid",
            "11111111",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));

        var verification = ProjectZomboidEnvironment.Verify(installation, required);

        Assert.False(verification.IsReady);
        Assert.Contains(
            verification.Issues,
            issue => issue.Code == "project-zomboid-version-mismatch");
    }

    private GameInstallation CreateInstallationWithDedicatedServerBuild(string buildId)
    {
        var library = Path.Combine(_root, $"environment-{Guid.NewGuid():N}");
        var clientRoot = Path.Combine(library, "steamapps", "common", "ProjectZomboid");
        var serverRoot = Path.Combine(library, "steamapps", "common", "Project Zomboid Dedicated Server");
        var userDataRoot = Path.Combine(library, "Zomboid");
        Directory.CreateDirectory(clientRoot);
        Directory.CreateDirectory(serverRoot);
        Directory.CreateDirectory(userDataRoot);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_380870.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\"\n{{\n    \"installdir\"    \"Project Zomboid Dedicated Server\"\n    \"buildid\"    \"{buildId}\"\n}}");
        return new GameInstallation(
            "project-zomboid:test",
            clientRoot,
            "steam",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProjectZomboidInstallationDiscovery.DedicatedServerRootPathKey] = serverRoot,
                [ProjectZomboidInstallationDiscovery.DedicatedServerManifestPathKey] = manifest,
                [ProjectZomboidInstallationDiscovery.DedicatedServerInstallStateKey] = "installed",
                [ProjectZomboidInstallationDiscovery.UserDataPathKey] = userDataRoot
            });
    }

    private static DetectedWorld CreateEnvironmentWorld(GameInstallation installation)
    {
        var userDataRoot = installation.Metadata![ProjectZomboidInstallationDiscovery.UserDataPathKey];
        var worldPath = Path.Combine(userDataRoot, "Saves", "Multiplayer", "servertest");
        var serverRoot = Path.Combine(userDataRoot, "Server");
        Directory.CreateDirectory(worldPath);
        Directory.CreateDirectory(serverRoot);
        File.WriteAllBytes(Path.Combine(worldPath, "map_t.bin"), [1]);
        File.WriteAllText(
            Path.Combine(serverRoot, "servertest.ini"),
            "PublicName=Steward Test\nWorkshopItems=\nMods=\n");
        return new DetectedWorld(worldPath, "servertest", worldPath);
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
