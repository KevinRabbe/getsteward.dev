using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.ProjectZomboid.Tests;

public sealed class ProjectZomboidModEnvironmentTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-pz-mods-{Guid.NewGuid():N}");

    [Fact]
    public void InspectionUsesSteamWorkshopContentManifestAsExactVersion()
    {
        var installation = CreateInstallation("9000");
        var world = CreateServerWorld(
            "servertest",
            workshopItems: "123456;789012",
            mods: @"\FirstMod;789012\SecondMod");
        InstallWorkshopItem("123456", "111111111111", "FirstMod");
        InstallWorkshopItem("789012", "222222222222", "SecondMod");

        var environment = ProjectZomboidEnvironment.Inspect(installation, world);

        Assert.Equal("9000", environment.GameVersion);
        Assert.Equal(2, environment.Components.Count);
        Assert.Collection(
            environment.Components.OrderBy(component => component.Id, StringComparer.Ordinal),
            component =>
            {
                Assert.Equal("123456", component.Id);
                Assert.Equal("111111111111", component.Version);
                Assert.Equal("steam-workshop", component.Kind);
                Assert.Equal("steam-workshop", component.Source);
            },
            component =>
            {
                Assert.Equal("789012", component.Id);
                Assert.Equal("222222222222", component.Version);
            });
    }

    [Fact]
    public void InspectionFailsClosedWhenConfiguredModIsNotProvidedBySelectedWorkshopItems()
    {
        var installation = CreateInstallation("9000");
        var world = CreateServerWorld(
            "servertest",
            workshopItems: "123456",
            mods: @"\FirstMod;\LocalOnlyMod");
        InstallWorkshopItem("123456", "111111111111", "FirstMod");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectZomboidEnvironment.Inspect(installation, world));

        Assert.Contains("LocalOnlyMod", exception.Message, StringComparison.Ordinal);
        Assert.Contains("will not guess", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VerificationBlocksStaleWorkshopManifest()
    {
        var installation = CreateInstallation("9000");
        InstallWorkshopItem("123456", "new-manifest", "FirstMod");
        var required = new EnvironmentManifest(
            1,
            "project-zomboid",
            "9000",
            [new EnvironmentComponent("steam-workshop", "123456", "old-manifest", "steam-workshop")],
            new Dictionary<string, string>(StringComparer.Ordinal));

        var verification = ProjectZomboidEnvironment.Verify(installation, required);

        Assert.False(verification.IsReady);
        Assert.Contains(
            verification.Issues,
            issue => issue.Code == "project-zomboid-workshop-manifest-mismatch");
    }

    [Fact]
    public void VerificationBlocksMissingWorkshopItem()
    {
        var installation = CreateInstallation("9000");
        WriteWorkshopManifest([]);
        var required = new EnvironmentManifest(
            1,
            "project-zomboid",
            "9000",
            [new EnvironmentComponent("steam-workshop", "123456", "111111", "steam-workshop")],
            new Dictionary<string, string>(StringComparer.Ordinal));

        var verification = ProjectZomboidEnvironment.Verify(installation, required);

        Assert.False(verification.IsReady);
        Assert.Contains(
            verification.Issues,
            issue => issue.Code == "project-zomboid-workshop-item-missing");
    }

    [Fact]
    public void InspectionRejectsDuplicateWorkshopItemsInServerConfig()
    {
        var installation = CreateInstallation("9000");
        var world = CreateServerWorld(
            "servertest",
            workshopItems: "123456;123456",
            mods: @"\FirstMod");
        InstallWorkshopItem("123456", "111111", "FirstMod");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectZomboidEnvironment.Inspect(installation, world));

        Assert.Contains("duplicate WorkshopItems", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private GameInstallation CreateInstallation(string buildId)
    {
        var clientRoot = Path.Combine(_root, "client");
        var serverRoot = ServerRoot();
        var userData = UserDataRoot();
        Directory.CreateDirectory(clientRoot);
        Directory.CreateDirectory(serverRoot);
        Directory.CreateDirectory(userData);

        var serverManifest = Path.Combine(_root, "steamapps", "appmanifest_380870.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(serverManifest)!);
        File.WriteAllText(
            serverManifest,
            $"\"AppState\"\n{{\n    \"installdir\" \"Project Zomboid Dedicated Server\"\n    \"buildid\" \"{buildId}\"\n}}");

        return new GameInstallation(
            "project-zomboid:test",
            clientRoot,
            "steam",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProjectZomboidInstallationDiscovery.DedicatedServerRootPathKey] = serverRoot,
                [ProjectZomboidInstallationDiscovery.DedicatedServerManifestPathKey] = serverManifest,
                [ProjectZomboidInstallationDiscovery.DedicatedServerInstallStateKey] = "installed",
                [ProjectZomboidInstallationDiscovery.UserDataPathKey] = userData
            });
    }

    private DetectedWorld CreateServerWorld(
        string serverName,
        string workshopItems,
        string mods)
    {
        var worldPath = Path.Combine(UserDataRoot(), "Saves", "Multiplayer", serverName);
        var serverConfigRoot = Path.Combine(UserDataRoot(), "Server");
        Directory.CreateDirectory(worldPath);
        Directory.CreateDirectory(serverConfigRoot);
        File.WriteAllBytes(Path.Combine(worldPath, "map_t.bin"), [1]);
        File.WriteAllText(
            Path.Combine(serverConfigRoot, serverName + ".ini"),
            $"PublicName=Test\nWorkshopItems={workshopItems}\nMods={mods}\n");
        return new DetectedWorld(worldPath, serverName, worldPath);
    }

    private void InstallWorkshopItem(
        string workshopId,
        string manifestId,
        params string[] modIds)
    {
        var contentPath = Path.Combine(
            ServerRoot(),
            "steamapps",
            "workshop",
            "content",
            "108600",
            workshopId);
        Directory.CreateDirectory(contentPath);
        foreach (var modId in modIds)
        {
            var modPath = Path.Combine(contentPath, "mods", modId);
            Directory.CreateDirectory(modPath);
            File.WriteAllText(
                Path.Combine(modPath, "mod.info"),
                $"name={modId}\nid={modId}\n");
        }

        var manifestPath = Path.Combine(
            ServerRoot(),
            "steamapps",
            "workshop",
            "appworkshop_108600.acf");
        var existing = new Dictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(manifestPath))
        {
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(
                         File.ReadAllText(manifestPath),
                         "\\\"(?<id>[0-9]+)\\\"\\s*\\{[^}]*\\\"manifest\\\"\\s+\\\"(?<manifest>[0-9A-Za-z-]+)\\\"[^}]*\\}",
                         System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                existing[match.Groups["id"].Value] = match.Groups["manifest"].Value;
            }
        }

        existing[workshopId] = manifestId;
        WriteWorkshopManifest(existing.Select(pair => (pair.Key, pair.Value)).ToArray());
    }

    private void WriteWorkshopManifest(params (string Id, string Manifest)[] items)
    {
        var manifestPath = Path.Combine(
            ServerRoot(),
            "steamapps",
            "workshop",
            "appworkshop_108600.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var itemBlocks = string.Join(
            Environment.NewLine,
            items.Select(item =>
                $"        \"{item.Id}\"\n        {{\n            \"size\" \"1\"\n            \"manifest\" \"{item.Manifest}\"\n        }}"));
        File.WriteAllText(
            manifestPath,
            $"\"AppWorkshop\"\n{{\n    \"appid\" \"108600\"\n    \"WorkshopItemsInstalled\"\n    {{\n{itemBlocks}\n    }}\n}}\n");
    }

    private string ServerRoot()
        => Path.Combine(_root, "server");

    private string UserDataRoot()
        => Path.Combine(_root, "Zomboid");

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
