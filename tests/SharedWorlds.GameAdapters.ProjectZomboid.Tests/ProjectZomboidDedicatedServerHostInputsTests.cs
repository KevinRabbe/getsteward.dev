using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.ProjectZomboid.Tests;

public sealed class ProjectZomboidDedicatedServerHostInputsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-pz-host-inputs-{Guid.NewGuid():N}");
    private readonly List<string> _ownedOperationRoots = [];

    [Fact]
    public void SingleRestoredBundleProducesIsolatedHostInputs()
    {
        var serverRoot = CreateServerRoot();
        var workspace = CreateOwnedWorkspace("steward-test");
        var world = Prepared(serverRoot, Path.Combine(serverRoot, "StartServer64.bat"), workspace);

        var inputs = ProjectZomboidWorldState.CreateDedicatedServerHostInputs(world);

        Assert.Equal(Path.Combine(serverRoot, "StartServer64.bat"), inputs.LaunchPath);
        Assert.Equal(serverRoot, inputs.WorkingDirectory);
        Assert.Equal(workspace, inputs.CacheDirectory);
        Assert.Equal("steward-test", inputs.ServerName);
        Assert.Equal(
            ["-cachedir=" + workspace, "-servername", "steward-test"],
            inputs.GameArguments);
    }

    [Fact]
    public void MultipleRestoredServerBundlesAreRejected()
    {
        var serverRoot = CreateServerRoot();
        var workspace = CreateOwnedWorkspace("first");
        AddServerBundle(workspace, "second");
        var world = Prepared(serverRoot, Path.Combine(serverRoot, "StartServer64.bat"), workspace);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectZomboidWorldState.CreateDedicatedServerHostInputs(world));

        Assert.Contains("exactly one authoritative multiplayer World", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LauncherOutsideDedicatedServerRootIsRejected()
    {
        var serverRoot = CreateServerRoot();
        var outsideLauncher = Path.Combine(_root, "outside", "StartServer64.bat");
        Directory.CreateDirectory(Path.GetDirectoryName(outsideLauncher)!);
        File.WriteAllText(outsideLauncher, "@echo off");
        var workspace = CreateOwnedWorkspace("steward-test");
        var world = Prepared(serverRoot, outsideLauncher, workspace);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectZomboidWorldState.CreateDedicatedServerHostInputs(world));

        Assert.Contains("outside the discovered dedicated-server root", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkedLauncherIsRejectedBeforeAnyProcessCanStart()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var serverRoot = Path.Combine(_root, "linked-launch-server");
        Directory.CreateDirectory(serverRoot);
        var outsideLauncher = Path.Combine(_root, "outside-launcher.bat");
        File.WriteAllText(outsideLauncher, "@echo off");
        var linkedLauncher = Path.Combine(serverRoot, "StartServer64.bat");
        File.CreateSymbolicLink(linkedLauncher, outsideLauncher);
        var workspace = CreateOwnedWorkspace("steward-test");
        var world = Prepared(serverRoot, linkedLauncher, workspace);
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                ProjectZomboidWorldState.CreateDedicatedServerHostInputs(world));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(linkedLauncher);
        }
    }

    [Fact]
    public void UnownedCacheDirectoryIsRejected()
    {
        var serverRoot = CreateServerRoot();
        var unowned = Path.Combine(_root, "unowned", "Zomboid");
        AddServerBundle(unowned, "steward-test");
        var world = Prepared(serverRoot, Path.Combine(serverRoot, "StartServer64.bat"), unowned);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProjectZomboidWorldState.CreateDedicatedServerHostInputs(world));

        Assert.Contains("Refusing to use unrecognized Project Zomboid Steward workspace", exception.Message, StringComparison.Ordinal);
    }

    private string CreateServerRoot()
    {
        var serverRoot = Path.GetFullPath(Path.Combine(_root, "server"));
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(Path.Combine(serverRoot, "StartServer64.bat"), "@echo off");
        return serverRoot;
    }

    private string CreateOwnedWorkspace(string serverName)
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.GetTempPath();
        }

        var operationRoot = Path.Combine(
            localData,
            "Steward",
            "workspaces",
            "project-zomboid",
            Guid.NewGuid().ToString("N"));
        _ownedOperationRoots.Add(operationRoot);
        var workspace = Path.Combine(operationRoot, "Zomboid");
        AddServerBundle(workspace, serverName);
        return Path.GetFullPath(workspace);
    }

    private static void AddServerBundle(string workspace, string serverName)
    {
        var worldRoot = Path.Combine(workspace, "Saves", "Multiplayer", serverName);
        Directory.CreateDirectory(worldRoot);
        File.WriteAllBytes(Path.Combine(worldRoot, "map_t.bin"), [1, 2, 3]);
        var serverConfigRoot = Path.Combine(workspace, "Server");
        Directory.CreateDirectory(serverConfigRoot);
        File.WriteAllText(Path.Combine(serverConfigRoot, serverName + ".ini"), "PublicName=Steward\n");
    }

    private static PreparedWorld Prepared(
        string serverRoot,
        string launchPath,
        string workspace)
    {
        var installation = new GameInstallation(
            "project-zomboid:test",
            serverRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProjectZomboidInstallationDiscovery.DedicatedServerInstallStateKey] = "installed",
                [ProjectZomboidInstallationDiscovery.DedicatedServerRootPathKey] = serverRoot,
                [ProjectZomboidInstallationDiscovery.DedicatedServerLaunchPathKey] = launchPath
            });
        var environment = new EnvironmentManifest(
            1,
            "project-zomboid",
            "test-build",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
        return new PreparedWorld(installation, workspace, environment);
    }

    public void Dispose()
    {
        foreach (var operationRoot in _ownedOperationRoots)
        {
            TryDeleteDirectory(operationRoot);
        }

        TryDeleteDirectory(_root);
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
