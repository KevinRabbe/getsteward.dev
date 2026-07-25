using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Palworld.Tests;

public sealed class PalworldDedicatedRuntimeInputSafetyTests : IDisposable
{
    private const string WorldId = "A1B2C3D4";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-palworld-runtime-input-safety-{Guid.NewGuid():N}");

    [Fact]
    public async Task InspectEnvironmentRejectsLinkedDedicatedServerManifest()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var installation = CreateRegularInstallation();
        var outsideManifest = Path.Combine(_root, "outside-appmanifest.acf");
        File.WriteAllText(outsideManifest, ManifestText("1000"));
        var linkedManifest = Path.Combine(_root, "linked-appmanifest.acf");
        File.CreateSymbolicLink(linkedManifest, outsideManifest);
        installation = WithMetadata(
            installation,
            PalworldInstallationDiscovery.DedicatedServerManifestPathKey,
            linkedManifest);
        try
        {
            var adapter = new PalworldAdapter();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.InspectEnvironmentAsync(
                    installation,
                    new DetectedWorld("palworld:test", "World", Path.Combine(_root, WorldId))));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Steam manifest", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(linkedManifest);
        }
    }

    [Fact]
    public async Task PrepareEnvironmentRejectsLinkedDedicatedServerRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var realServerRoot = CreateServerRoot("real-server");
        var linkedServerRoot = Path.Combine(_root, "linked-server");
        Directory.CreateSymbolicLink(linkedServerRoot, realServerRoot);
        var installation = CreateInstallation(linkedServerRoot, Path.Combine(realServerRoot, "PalServer.exe"));
        try
        {
            var adapter = new PalworldAdapter();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.PrepareEnvironmentAsync(installation, Environment()));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dedicated-server root", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(linkedServerRoot);
        }
    }

    [Fact]
    public async Task PrepareEnvironmentRejectsLinkedDedicatedServerExecutable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var serverRoot = CreateServerRoot();
        var realExecutable = Path.Combine(_root, "outside-PalServer.exe");
        File.WriteAllBytes(realExecutable, [1]);
        var linkedExecutable = Path.Combine(serverRoot, "PalServer.exe");
        File.Delete(linkedExecutable);
        File.CreateSymbolicLink(linkedExecutable, realExecutable);
        var installation = CreateInstallation(serverRoot, linkedExecutable);
        try
        {
            var adapter = new PalworldAdapter();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.PrepareEnvironmentAsync(installation, Environment()));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("executable", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(linkedExecutable);
        }
    }

    [Fact]
    public async Task PrepareEnvironmentRejectsLinkedSaveGamesDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var installation = CreateRegularInstallation();
        var savedRoot = Path.Combine(ServerRoot(installation), "Pal", "Saved");
        Directory.CreateDirectory(savedRoot);
        var outsideSaveGames = Path.Combine(_root, "outside-save-games");
        Directory.CreateDirectory(outsideSaveGames);
        var linkedSaveGames = Path.Combine(savedRoot, "SaveGames");
        Directory.CreateSymbolicLink(linkedSaveGames, outsideSaveGames);
        try
        {
            var adapter = new PalworldAdapter();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.PrepareEnvironmentAsync(installation, Environment()));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SaveGames", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(linkedSaveGames);
        }
    }

    [Fact]
    public async Task PrepareEnvironmentCreatesRegularNativeSaveProfileChain()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var installation = CreateRegularInstallation();
        var adapter = new PalworldAdapter();

        var prepared = await adapter.PrepareEnvironmentAsync(installation, Environment());

        var profileRoot = Path.Combine(ServerRoot(installation), "Pal", "Saved", "SaveGames", "0");
        Assert.True(Directory.Exists(profileRoot));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(profileRoot, WorldId)),
            Path.GetFullPath(prepared.WorkingDirectory));
        Assert.Equal(
            0,
            (int)(File.GetAttributes(profileRoot) & FileAttributes.ReparsePoint));
    }

    [Fact]
    public async Task LaunchHostRejectsLinkedPreparedLevelSaveBeforeProcessLaunch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var prepared = CreatePreparedWorld();
        var level = Path.Combine(prepared.WorkingDirectory, "Level.sav");
        var outside = Path.Combine(_root, "outside-Level.sav");
        File.WriteAllBytes(outside, [1, 2, 3]);
        File.Delete(level);
        File.CreateSymbolicLink(level, outside);
        try
        {
            var adapter = new PalworldAdapter();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.LaunchHostAsync(prepared));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Level.sav", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(level);
        }
    }

    [Fact]
    public async Task LaunchHostRejectsLinkedWorldOptionBeforeProcessLaunch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var prepared = CreatePreparedWorld();
        var worldOption = Path.Combine(prepared.WorkingDirectory, "WorldOption.sav");
        var outside = Path.Combine(_root, "outside-WorldOption.sav");
        File.WriteAllBytes(outside, [1, 2, 3]);
        File.Delete(worldOption);
        File.CreateSymbolicLink(worldOption, outside);
        try
        {
            var adapter = new PalworldAdapter();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.LaunchHostAsync(prepared));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("WorldOption.sav", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(worldOption);
        }
    }

    [Theory]
    [InlineData("GameUserSettings.ini")]
    [InlineData("PalWorldSettings.ini")]
    public async Task LaunchHostRejectsLinkedManagedConfigurationBeforeProcessLaunch(string fileName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var prepared = CreatePreparedWorld();
        var configRoot = Path.Combine(
            ServerRoot(prepared.Installation),
            "Pal",
            "Saved",
            "Config",
            "WindowsServer");
        var path = Path.Combine(configRoot, fileName);
        var outside = Path.Combine(_root, "outside-" + fileName);
        File.WriteAllText(outside, "[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(DedicatedServerName=A1B2C3D4)");
        File.Delete(path);
        File.CreateSymbolicLink(path, outside);
        try
        {
            var adapter = new PalworldAdapter();
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                adapter.LaunchHostAsync(prepared));

            Assert.Contains("linked or a reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(fileName, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("GameUserSettings.ini")]
    [InlineData("PalWorldSettings.ini")]
    public async Task LaunchHostRejectsOversizedManagedConfigurationBeforeRead(string fileName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var prepared = CreatePreparedWorld();
        var path = Path.Combine(
            ServerRoot(prepared.Installation),
            "Pal",
            "Saved",
            "Config",
            "WindowsServer",
            fileName);
        using (var stream = new FileStream(
                   path,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.SetLength(PalworldDedicatedRuntimeInputSafety.MaximumManagedConfigurationBytes + 1L);
        }

        var adapter = new PalworldAdapter();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.LaunchHostAsync(prepared));

        Assert.Contains(fileName, exception.Message, StringComparison.Ordinal);
        Assert.Contains("safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            PalworldDedicatedRuntimeInputSafety.MaximumManagedConfigurationBytes + 1L,
            new FileInfo(path).Length);
    }

    private PreparedWorld CreatePreparedWorld()
    {
        var installation = CreateRegularInstallation();
        var worldRoot = Path.Combine(
            ServerRoot(installation),
            "Pal",
            "Saved",
            "SaveGames",
            "0",
            WorldId);
        Directory.CreateDirectory(worldRoot);
        File.WriteAllBytes(Path.Combine(worldRoot, "Level.sav"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(worldRoot, "WorldOption.sav"), [1, 2, 3]);

        var configRoot = Path.Combine(
            ServerRoot(installation),
            "Pal",
            "Saved",
            "Config",
            "WindowsServer");
        Directory.CreateDirectory(configRoot);
        File.WriteAllText(
            Path.Combine(configRoot, "GameUserSettings.ini"),
            "[/Script/Pal.PalGameLocalSettings]\nDedicatedServerName=A1B2C3D4\n");
        File.WriteAllText(
            Path.Combine(configRoot, "PalWorldSettings.ini"),
            "[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(DedicatedServerName=A1B2C3D4)\n");

        return new PreparedWorld(
            installation,
            worldRoot,
            Environment());
    }

    private GameInstallation CreateRegularInstallation()
    {
        var serverRoot = CreateServerRoot();
        var manifest = Path.Combine(_root, "steamapps", "appmanifest_2394010.acf");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, ManifestText("1000"));
        var installation = CreateInstallation(serverRoot, Path.Combine(serverRoot, "PalServer.exe"));
        return WithMetadata(
            installation,
            PalworldInstallationDiscovery.DedicatedServerManifestPathKey,
            manifest);
    }

    private string CreateServerRoot(string name = "server")
    {
        var serverRoot = Path.Combine(_root, name);
        Directory.CreateDirectory(serverRoot);
        File.WriteAllBytes(Path.Combine(serverRoot, "PalServer.exe"), [1]);
        return serverRoot;
    }

    private static EnvironmentManifest Environment()
        => new(
            SchemaVersion: 1,
            AdapterId: "palworld",
            GameVersion: "1000",
            Components: [],
            Configuration: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["hostingMode"] = "dedicated-server",
                ["dedicatedServerName"] = WorldId
            });

    private static string ManifestText(string buildId)
        => $"\"AppState\"\n{{\n    \"buildid\" \"{buildId}\"\n}}";

    private static GameInstallation CreateInstallation(string serverRoot, string serverExecutable)
        => new(
            "palworld:test",
            serverRoot,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PalworldInstallationDiscovery.DedicatedServerRootPathKey] = serverRoot,
                [PalworldInstallationDiscovery.DedicatedServerExecutablePathKey] = serverExecutable,
                [PalworldInstallationDiscovery.DedicatedServerInstallStateKey] = "installed"
            });

    private static GameInstallation WithMetadata(
        GameInstallation installation,
        string key,
        string value)
    {
        var metadata = installation.Metadata is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(installation.Metadata, StringComparer.Ordinal);
        metadata[key] = value;
        return installation with { Metadata = metadata };
    }

    private static string ServerRoot(GameInstallation installation)
        => installation.Metadata![PalworldInstallationDiscovery.DedicatedServerRootPathKey];

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
