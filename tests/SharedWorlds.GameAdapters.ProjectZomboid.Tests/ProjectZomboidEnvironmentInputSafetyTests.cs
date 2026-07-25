using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.ProjectZomboid.Tests;

public sealed class ProjectZomboidEnvironmentInputSafetyTests : IDisposable
{
    private const string ServerName = "servertest";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-pz-environment-input-safety-{Guid.NewGuid():N}");

    [Fact]
    public void InspectionRejectsLinkedDedicatedServerRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var realServerRoot = Path.Combine(_root, "real-server");
        Directory.CreateDirectory(realServerRoot);
        var linkedServerRoot = Path.Combine(_root, "linked-server");
        Directory.CreateSymbolicLink(linkedServerRoot, realServerRoot);
        var userData = CreateUserData();
        var installation = CreateInstallation(linkedServerRoot, CreateServerManifest("9000"), userData);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                ProjectZomboidEnvironment.Inspect(installation, World()));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Dedicated Server root", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(linkedServerRoot);
        }
    }

    [Fact]
    public void InspectionRejectsLinkedSteamManifest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var serverRoot = CreateServerRoot();
        var outsideManifest = Path.Combine(_root, "outside-appmanifest.acf");
        File.WriteAllText(outsideManifest, ServerManifestText("9000"));
        var linkedManifest = Path.Combine(_root, "linked-appmanifest.acf");
        File.CreateSymbolicLink(linkedManifest, outsideManifest);
        var installation = CreateInstallation(serverRoot, linkedManifest, CreateUserData());
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                ProjectZomboidEnvironment.Inspect(installation, World()));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Steam manifest", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(linkedManifest);
        }
    }

    [Fact]
    public void InspectionRejectsLinkedUserDataRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var realUserData = CreateUserData("real-user-data");
        var linkedUserData = Path.Combine(_root, "linked-user-data");
        Directory.CreateSymbolicLink(linkedUserData, realUserData);
        var installation = CreateInstallation(CreateServerRoot(), CreateServerManifest("9000"), linkedUserData);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                ProjectZomboidEnvironment.Inspect(installation, World()));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("user-data root", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(linkedUserData);
        }
    }

    [Fact]
    public void InspectionRejectsLinkedServerConfigurationDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = Path.Combine(_root, "user-data-linked-server-dir");
        Directory.CreateDirectory(userData);
        var outsideServer = Path.Combine(_root, "outside-server-config");
        Directory.CreateDirectory(outsideServer);
        File.WriteAllText(Path.Combine(outsideServer, ServerName + ".ini"), ServerConfig());
        var linkedServer = Path.Combine(userData, "Server");
        Directory.CreateSymbolicLink(linkedServer, outsideServer);
        var installation = CreateInstallation(CreateServerRoot(), CreateServerManifest("9000"), userData);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                ProjectZomboidEnvironment.Inspect(installation, World()));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Server configuration directory", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(linkedServer);
        }
    }

    [Fact]
    public void InspectionRejectsLinkedServerDefinition()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = Path.Combine(_root, "user-data-linked-config");
        var serverConfigRoot = Path.Combine(userData, "Server");
        Directory.CreateDirectory(serverConfigRoot);
        var outsideConfig = Path.Combine(_root, "outside-server.ini");
        File.WriteAllText(outsideConfig, ServerConfig());
        var linkedConfig = Path.Combine(serverConfigRoot, ServerName + ".ini");
        File.CreateSymbolicLink(linkedConfig, outsideConfig);
        var installation = CreateInstallation(CreateServerRoot(), CreateServerManifest("9000"), userData);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                ProjectZomboidEnvironment.Inspect(installation, World()));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("server definition", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(linkedConfig);
        }
    }

    [Fact]
    public void InspectionRejectsLinkedWorkshopRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var serverRoot = CreateServerRoot();
        var steamAppsRoot = Path.Combine(serverRoot, "steamapps");
        Directory.CreateDirectory(steamAppsRoot);
        var outsideWorkshop = Path.Combine(_root, "outside-workshop");
        WriteWorkshopItem(outsideWorkshop, "123456", "111111", "ExampleMod");
        var linkedWorkshop = Path.Combine(steamAppsRoot, "workshop");
        Directory.CreateSymbolicLink(linkedWorkshop, outsideWorkshop);
        var installation = CreateInstallation(
            serverRoot,
            CreateServerManifest("9000"),
            CreateUserData(workshopItems: "123456", mods: "ExampleMod"));
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                ProjectZomboidEnvironment.Inspect(installation, World()));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Workshop directory", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(linkedWorkshop);
        }
    }

    [Fact]
    public void InspectionRejectsLinkedWorkshopManifest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var serverRoot = CreateServerRoot();
        var workshopRoot = Path.Combine(serverRoot, "steamapps", "workshop");
        Directory.CreateDirectory(workshopRoot);
        var outsideManifest = Path.Combine(_root, "outside-workshop.acf");
        File.WriteAllText(outsideManifest, WorkshopManifest("123456", "111111"));
        var linkedManifest = Path.Combine(workshopRoot, "appworkshop_108600.acf");
        File.CreateSymbolicLink(linkedManifest, outsideManifest);
        var installation = CreateInstallation(
            serverRoot,
            CreateServerManifest("9000"),
            CreateUserData(workshopItems: "123456", mods: "ExampleMod"));
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                ProjectZomboidEnvironment.Inspect(installation, World()));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Workshop manifest", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(linkedManifest);
        }
    }

    [Fact]
    public void InspectionRejectsLinkedWorkshopContentRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var serverRoot = CreateServerRoot();
        var workshopRoot = Path.Combine(serverRoot, "steamapps", "workshop");
        Directory.CreateDirectory(workshopRoot);
        File.WriteAllText(
            Path.Combine(workshopRoot, "appworkshop_108600.acf"),
            WorkshopManifest("123456", "111111"));
        var outsideContent = Path.Combine(_root, "outside-content");
        WriteWorkshopContent(outsideContent, "123456", "ExampleMod");
        var linkedContent = Path.Combine(workshopRoot, "content");
        Directory.CreateSymbolicLink(linkedContent, outsideContent);
        var installation = CreateInstallation(
            serverRoot,
            CreateServerManifest("9000"),
            CreateUserData(workshopItems: "123456", mods: "ExampleMod"));
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                ProjectZomboidEnvironment.Inspect(installation, World()));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Workshop content directory", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(linkedContent);
        }
    }

    [Fact]
    public void RegularOwnedEnvironmentStillInspectsSuccessfully()
    {
        var serverRoot = CreateServerRoot();
        var workshopRoot = Path.Combine(serverRoot, "steamapps", "workshop");
        WriteWorkshopItem(workshopRoot, "123456", "111111", "ExampleMod");
        var installation = CreateInstallation(
            serverRoot,
            CreateServerManifest("9000"),
            CreateUserData(workshopItems: "123456", mods: "ExampleMod"));

        var environment = ProjectZomboidEnvironment.Inspect(installation, World());

        Assert.Equal("9000", environment.GameVersion);
        var component = Assert.Single(environment.Components);
        Assert.Equal("123456", component.Id);
        Assert.Equal("111111", component.Version);
    }

    private string CreateServerRoot()
    {
        var serverRoot = Path.Combine(_root, "server");
        Directory.CreateDirectory(serverRoot);
        return serverRoot;
    }

    private string CreateServerManifest(string buildId)
    {
        var manifest = Path.Combine(_root, "steamapps", "appmanifest_380870.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, ServerManifestText(buildId));
        return manifest;
    }

    private static string ServerManifestText(string buildId)
        => $"\"AppState\"\n{{\n    \"installdir\" \"Project Zomboid Dedicated Server\"\n    \"buildid\" \"{buildId}\"\n}}";

    private string CreateUserData(
        string name = "Zomboid",
        string workshopItems = "",
        string mods = "")
    {
        var userData = Path.Combine(_root, name);
        var serverConfigRoot = Path.Combine(userData, "Server");
        Directory.CreateDirectory(serverConfigRoot);
        File.WriteAllText(
            Path.Combine(serverConfigRoot, ServerName + ".ini"),
            ServerConfig(workshopItems, mods));
        return userData;
    }

    private static string ServerConfig(string workshopItems = "", string mods = "")
        => $"PublicName=Steward\nWorkshopItems={workshopItems}\nMods={mods}\n";

    private static void WriteWorkshopItem(
        string workshopRoot,
        string workshopId,
        string manifestId,
        string modId)
    {
        Directory.CreateDirectory(workshopRoot);
        File.WriteAllText(
            Path.Combine(workshopRoot, "appworkshop_108600.acf"),
            WorkshopManifest(workshopId, manifestId));
        WriteWorkshopContent(Path.Combine(workshopRoot, "content"), workshopId, modId);
    }

    private static void WriteWorkshopContent(
        string contentRoot,
        string workshopId,
        string modId)
    {
        var modRoot = Path.Combine(contentRoot, "108600", workshopId, "mods", modId);
        Directory.CreateDirectory(modRoot);
        File.WriteAllText(
            Path.Combine(modRoot, "mod.info"),
            $"name={modId}\nid={modId}\n");
    }

    private static string WorkshopManifest(string workshopId, string manifestId)
        => $"\"AppWorkshop\"\n{{\n    \"appid\" \"108600\"\n    \"WorkshopItemsInstalled\"\n    {{\n        \"{workshopId}\"\n        {{\n            \"size\" \"1\"\n            \"manifest\" \"{manifestId}\"\n        }}\n    }}\n}}\n";

    private static DetectedWorld World()
        => new("world:test", ServerName, Path.Combine("C:\\", "Steward", ServerName));

    private static GameInstallation CreateInstallation(
        string serverRoot,
        string manifestPath,
        string userDataRoot)
        => new(
            "project-zomboid:test",
            serverRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProjectZomboidInstallationDiscovery.DedicatedServerRootPathKey] = serverRoot,
                [ProjectZomboidInstallationDiscovery.DedicatedServerManifestPathKey] = manifestPath,
                [ProjectZomboidInstallationDiscovery.DedicatedServerInstallStateKey] = "installed",
                [ProjectZomboidInstallationDiscovery.UserDataPathKey] = userDataRoot
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
