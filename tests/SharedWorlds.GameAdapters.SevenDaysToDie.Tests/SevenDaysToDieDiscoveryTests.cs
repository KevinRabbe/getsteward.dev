using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.SevenDaysToDie.Tests;

public sealed class SevenDaysToDieDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-7dtd-discovery-{Guid.NewGuid():N}");

    [Fact]
    public void SteamDiscoveryPairsClientWithDedicatedServerAcrossLibraries()
    {
        var clientLibrary = Path.Combine(_root, "client-library");
        var serverLibrary = Path.Combine(_root, "server-library");
        var clientRoot = Path.Combine(clientLibrary, "steamapps", "common", "Custom Client");
        var serverRoot = Path.Combine(serverLibrary, "steamapps", "common", "Custom Server");
        Directory.CreateDirectory(clientRoot);
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(Path.Combine(clientRoot, "7DaysToDie.exe"), string.Empty);
        File.WriteAllText(Path.Combine(serverRoot, "7DaysToDieServer.exe"), string.Empty);

        Directory.CreateDirectory(Path.Combine(clientLibrary, "steamapps"));
        Directory.CreateDirectory(Path.Combine(serverLibrary, "steamapps"));
        File.WriteAllText(
            Path.Combine(clientLibrary, "steamapps", "appmanifest_251570.acf"),
            "\"AppState\"\n{\n    \"installdir\"    \"Custom Client\"\n    \"buildid\"    \"12345678\"\n}");
        var serverManifest = Path.Combine(serverLibrary, "steamapps", "appmanifest_294420.acf");
        File.WriteAllText(
            serverManifest,
            "\"AppState\"\n{\n    \"installdir\"    \"Custom Server\"\n    \"buildid\"    \"87654321\"\n}");

        var userData = Path.Combine(_root, "user-data");
        var installations = SevenDaysToDieInstallationDiscovery.DiscoverFromSteamLibraries(
            [clientLibrary, serverLibrary],
            userData);

        var installation = Assert.Single(installations);
        Assert.Equal(Path.GetFullPath(clientRoot), installation.RootPath);
        Assert.Equal("steam", installation.Source);
        Assert.NotNull(installation.Metadata);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(clientRoot, "7DaysToDie.exe")),
            installation.Metadata[SevenDaysToDieInstallationDiscovery.ClientExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(serverRoot),
            installation.Metadata[SevenDaysToDieInstallationDiscovery.DedicatedServerRootPathKey]);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(serverRoot, "7DaysToDieServer.exe")),
            installation.Metadata[SevenDaysToDieInstallationDiscovery.DedicatedServerExecutablePathKey]);
        Assert.Equal(
            Path.GetFullPath(serverManifest),
            installation.Metadata[SevenDaysToDieInstallationDiscovery.DedicatedServerManifestPathKey]);
        Assert.Equal("installed", installation.Metadata[SevenDaysToDieInstallationDiscovery.DedicatedServerInstallStateKey]);
        Assert.Equal(Path.GetFullPath(userData), installation.Metadata[SevenDaysToDieInstallationDiscovery.UserDataPathKey]);
    }

    [Fact]
    public void SteamDiscoveryKeepsClientUsableWhenDedicatedServerIsMissing()
    {
        var library = Path.Combine(_root, "library");
        var clientRoot = Path.Combine(library, "steamapps", "common", "7 Days To Die");
        Directory.CreateDirectory(clientRoot);
        Directory.CreateDirectory(Path.Combine(library, "steamapps"));
        File.WriteAllText(Path.Combine(clientRoot, "7DaysToDie.exe"), string.Empty);

        var installations = SevenDaysToDieInstallationDiscovery.DiscoverFromSteamLibraries(
            [library],
            Path.Combine(_root, "user-data"));

        var installation = Assert.Single(installations);
        Assert.Equal(
            "not-installed-in-discovered-steam-libraries",
            installation.Metadata![SevenDaysToDieInstallationDiscovery.DedicatedServerInstallStateKey]);
        Assert.False(installation.Metadata.ContainsKey(SevenDaysToDieInstallationDiscovery.DedicatedServerExecutablePathKey));
    }

    [Fact]
    public void WorldDiscoveryRequiresTwoLevelSaveWithMainTtw()
    {
        var userData = Path.Combine(_root, "user-data");
        var validSave = Path.Combine(userData, "Saves", "Navezgane", "Steward Test");
        var invalidSave = Path.Combine(userData, "Saves", "Navezgane", "Incomplete");
        Directory.CreateDirectory(validSave);
        Directory.CreateDirectory(invalidSave);
        File.WriteAllText(Path.Combine(validSave, "main.ttw"), "world-header");
        Directory.CreateDirectory(Path.Combine(validSave, "Player"));
        Directory.CreateDirectory(Path.Combine(validSave, "Region"));

        var installation = new GameInstallation(
            "7-days-to-die:test",
            _root,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SevenDaysToDieInstallationDiscovery.UserDataPathKey] = userData
            });

        var worlds = SevenDaysToDieWorldDiscovery.Discover(installation);

        var world = Assert.Single(worlds);
        Assert.Equal(Path.GetFullPath(validSave), world.Id);
        Assert.Equal(Path.GetFullPath(validSave), world.SourcePath);
        Assert.Equal("Steward Test (Navezgane)", world.DisplayName);
    }

    [Fact]
    public void WorldDiscoveryReturnsEmptyWhenUserDataMetadataIsMissing()
    {
        var installation = new GameInstallation("7-days-to-die:test", _root, "test");

        Assert.Empty(SevenDaysToDieWorldDiscovery.Discover(installation));
    }

    [Fact]
    public void EnvironmentInspectionUsesDedicatedServerSteamBuildId()
    {
        var installation = CreateInstallationWithDedicatedServerBuild("87654321");

        var environment = SevenDaysToDieEnvironment.Inspect(installation);

        Assert.Equal(1, environment.SchemaVersion);
        Assert.Equal("7-days-to-die", environment.AdapterId);
        Assert.Equal("87654321", environment.GameVersion);
        Assert.Empty(environment.Components);
        Assert.Empty(environment.Configuration);
    }

    [Fact]
    public void EnvironmentInspectionFailsClosedWithoutDedicatedServerManifest()
    {
        var installation = new GameInstallation(
            "7-days-to-die:test",
            _root,
            "steam",
            new Dictionary<string, string>(StringComparer.Ordinal));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SevenDaysToDieEnvironment.Inspect(installation));

        Assert.Contains("Dedicated Server", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentVerificationFailsClosedOnDedicatedServerBuildMismatch()
    {
        var installation = CreateInstallationWithDedicatedServerBuild("22222222");
        var required = new EnvironmentManifest(
            1,
            "7-days-to-die",
            "11111111",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));

        var verification = SevenDaysToDieEnvironment.Verify(installation, required);

        Assert.False(verification.IsReady);
        Assert.Contains(
            verification.Issues,
            issue => issue.Code == "7dtd-version-mismatch");
    }

    private GameInstallation CreateInstallationWithDedicatedServerBuild(string buildId)
    {
        var library = Path.Combine(_root, $"environment-{Guid.NewGuid():N}");
        var root = Path.Combine(library, "steamapps", "common", "7 Days To Die");
        var serverRoot = Path.Combine(library, "steamapps", "common", "7 Days to Die Dedicated Server");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(serverRoot);
        var manifest = Path.Combine(library, "steamapps", "appmanifest_294420.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(
            manifest,
            $"\"AppState\"\n{{\n    \"installdir\"    \"7 Days to Die Dedicated Server\"\n    \"buildid\"    \"{buildId}\"\n}}");
        return new GameInstallation(
            "7-days-to-die:test",
            root,
            "steam",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SevenDaysToDieInstallationDiscovery.DedicatedServerRootPathKey] = serverRoot,
                [SevenDaysToDieInstallationDiscovery.DedicatedServerManifestPathKey] = manifest,
                [SevenDaysToDieInstallationDiscovery.DedicatedServerInstallStateKey] = "installed"
            });
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
