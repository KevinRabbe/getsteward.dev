using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.ProjectZomboid.Tests;

public sealed class ProjectZomboidWorldStateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-pz-state-{Guid.NewGuid():N}");
    private readonly List<string> _packages = [];
    private readonly List<string> _ownedWorkspaces = [];

    [Fact]
    public async Task CaptureIncludesOnlySelectedWorldConfigAndDatabase()
    {
        var userData = CreateServerBundle("servertest");
        var otherWorld = Path.Combine(userData, "Saves", "Multiplayer", "other");
        Directory.CreateDirectory(otherWorld);
        await File.WriteAllBytesAsync(Path.Combine(otherWorld, "map_t.bin"), [99]);
        Directory.CreateDirectory(Path.Combine(userData, "Server"));
        await File.WriteAllTextAsync(Path.Combine(userData, "Server", "other.ini"), "PublicName=Other");
        var remoteCache = Path.Combine(userData, "Saves", "Multiplayer", "10.0.0.2_16261_hash");
        Directory.CreateDirectory(remoteCache);
        await File.WriteAllBytesAsync(Path.Combine(remoteCache, "map_t.bin"), [88]);

        var installation = Installation(userData);
        var worldPath = Path.Combine(userData, "Saves", "Multiplayer", "servertest");
        var detected = new DetectedWorld(worldPath, "servertest", worldPath);

        var captured = await ProjectZomboidWorldState.CaptureDetectedWorldAsync(
            installation,
            detected,
            CancellationToken.None);
        _packages.Add(captured.Package.Path);

        using var archive = ZipFile.OpenRead(captured.Package.Path);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("Saves/Multiplayer/servertest/map_t.bin", names);
        Assert.Contains("Saves/Multiplayer/servertest/players.db", names);
        Assert.Contains("Server/servertest.ini", names);
        Assert.Contains("Server/servertest_SandboxVars.lua", names);
        Assert.Contains("Server/servertest_spawnpoints.lua", names);
        Assert.Contains("Server/servertest_spawnregions.lua", names);
        Assert.Contains("db/servertest.db", names);
        Assert.DoesNotContain(names, name => name.Contains("other", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("10.0.0.2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CaptureAndRestorePreserveServerBundleBytes()
    {
        var userData = CreateServerBundle("steward");
        var installation = Installation(userData);
        var worldPath = Path.Combine(userData, "Saves", "Multiplayer", "steward");
        var captured = await ProjectZomboidWorldState.CaptureDetectedWorldAsync(
            installation,
            new DetectedWorld(worldPath, "steward", worldPath),
            CancellationToken.None);
        _packages.Add(captured.Package.Path);

        var prepared = CreateOwnedPrepared(installation);
        var destination = prepared.WorkingDirectory;
        await ProjectZomboidWorldState.RestorePreparedWorldAsync(
            prepared,
            captured.Package,
            CancellationToken.None);

        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(userData, "Saves", "Multiplayer", "steward", "map_t.bin")),
            await File.ReadAllBytesAsync(Path.Combine(destination, "Saves", "Multiplayer", "steward", "map_t.bin")));
        Assert.Equal(
            await File.ReadAllTextAsync(Path.Combine(userData, "Server", "steward.ini")),
            await File.ReadAllTextAsync(Path.Combine(destination, "Server", "steward.ini")));
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(userData, "db", "steward.db")),
            await File.ReadAllBytesAsync(Path.Combine(destination, "db", "steward.db")));
    }

    [Fact]
    public async Task CaptureRejectsLinkedWorldDirectoryInsteadOfFollowingOutsideWorld()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = CreateServerBundle("linked-world");
        var worldPath = Path.Combine(userData, "Saves", "Multiplayer", "linked-world");
        var outside = Path.Combine(_root, "outside-linked-pz-world");
        Directory.CreateDirectory(outside);
        await File.WriteAllBytesAsync(Path.Combine(outside, "outside.bin"), [9, 9, 9]);

        var linkedDirectory = Path.Combine(worldPath, "LinkedOutside");
        Directory.CreateSymbolicLink(linkedDirectory, outside);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ProjectZomboidWorldState.CaptureDetectedWorldAsync(
                    Installation(userData),
                    new DetectedWorld(worldPath, "linked-world", worldPath),
                    CancellationToken.None));

            Assert.Contains("linked or reparse-point", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(linkedDirectory);
        }
    }

    [Fact]
    public async Task CaptureRejectsLinkedServerConfiguration()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var userData = CreateServerBundle("linked-config");
        var worldPath = Path.Combine(userData, "Saves", "Multiplayer", "linked-config");
        var configPath = Path.Combine(userData, "Server", "linked-config.ini");
        var outsideConfig = Path.Combine(_root, "outside-linked-config.ini");
        await File.WriteAllTextAsync(outsideConfig, "PublicName=Outside");
        File.Delete(configPath);
        File.CreateSymbolicLink(configPath, outsideConfig);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ProjectZomboidWorldState.CaptureDetectedWorldAsync(
                    Installation(userData),
                    new DetectedWorld(worldPath, "linked-config", worldPath),
                    CancellationToken.None));

            Assert.Contains("linked or reparse-point", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task RestoreRejectsAdditionalServerPresetWithoutReplacingWorkspaceIdentity()
    {
        var package = Path.Combine(_root, "multiple-server-presets.zip");
        Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            await WriteEntryAsync(archive, "Saves/Multiplayer/servertest/map_t.bin", [1]);
            await WriteTextEntryAsync(archive, "Server/servertest.ini", "PublicName=Primary");
            await WriteTextEntryAsync(archive, "Server/other.ini", "PublicName=Other");
        }

        var prepared = CreateOwnedPrepared(Installation(Path.Combine(_root, "unused")));
        var destination = prepared.WorkingDirectory;
        await File.WriteAllTextAsync(Path.Combine(destination, "sentinel.txt"), "original");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProjectZomboidWorldState.RestorePreparedWorldAsync(
                prepared,
                new StatePackage("multiple", package),
                CancellationToken.None));

        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(destination, "sentinel.txt")));
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public async Task RestoreRejectsPathTraversalWithoutReplacingWorkspaceIdentity()
    {
        var package = Path.Combine(_root, "malicious.zip");
        Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            await WriteEntryAsync(archive, "Saves/Multiplayer/servertest/map_t.bin", [1]);
            await WriteTextEntryAsync(archive, "Server/servertest.ini", "PublicName=Primary");
            await WriteEntryAsync(archive, "../escaped.txt", [2]);
        }

        var prepared = CreateOwnedPrepared(Installation(Path.Combine(_root, "unused-2")));
        var destination = prepared.WorkingDirectory;
        await File.WriteAllTextAsync(Path.Combine(destination, "sentinel.txt"), "original");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProjectZomboidWorldState.RestorePreparedWorldAsync(
                prepared,
                new StatePackage("malicious", package),
                CancellationToken.None));

        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(destination, "sentinel.txt")));
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(destination)!.FullName, "escaped.txt")));
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public async Task FinalizePreservesRecoveryWorkspaceButDeletesDiscardedWorkspace()
    {
        var preparedPreserve = CreateOwnedPrepared(Installation(Path.Combine(_root, "unused-3")));
        var preserveRoot = preparedPreserve.WorkingDirectory;

        await ProjectZomboidWorldState.FinalizePreparedWorldAsync(
            preparedPreserve,
            PreparedWorldDisposition.PreserveForRecovery,
            CancellationToken.None);
        Assert.True(Directory.Exists(preserveRoot));

        var preparedDiscard = CreateOwnedPrepared(Installation(Path.Combine(_root, "unused-4")));
        var discardRoot = preparedDiscard.WorkingDirectory;

        await ProjectZomboidWorldState.FinalizePreparedWorldAsync(
            preparedDiscard,
            PreparedWorldDisposition.Discard,
            CancellationToken.None);
        _ownedWorkspaces.Remove(discardRoot);
        Assert.False(Directory.Exists(discardRoot));
    }

    private string CreateServerBundle(string serverName)
    {
        var userData = Path.Combine(_root, $"user-data-{Guid.NewGuid():N}");
        var world = Path.Combine(userData, "Saves", "Multiplayer", serverName);
        var server = Path.Combine(userData, "Server");
        var db = Path.Combine(userData, "db");
        Directory.CreateDirectory(world);
        Directory.CreateDirectory(server);
        Directory.CreateDirectory(db);
        File.WriteAllBytes(Path.Combine(world, "map_t.bin"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(world, "players.db"), [4, 5, 6]);
        File.WriteAllText(Path.Combine(server, serverName + ".ini"), "PublicName=Steward\nPassword=secret");
        File.WriteAllText(Path.Combine(server, serverName + "_SandboxVars.lua"), "SandboxVars = { VERSION = 5 }");
        File.WriteAllText(Path.Combine(server, serverName + "_spawnpoints.lua"), "function SpawnPoints() return {} end");
        File.WriteAllText(Path.Combine(server, serverName + "_spawnregions.lua"), "function SpawnRegions() return {} end");
        File.WriteAllBytes(Path.Combine(db, serverName + ".db"), [7, 8, 9]);
        return userData;
    }

    private PreparedWorld CreateOwnedPrepared(GameInstallation installation)
    {
        var workspace = ProjectZomboidWorkspaceOwnership.Create();
        _ownedWorkspaces.Add(workspace);
        return new PreparedWorld(
            installation,
            workspace,
            EmptyEnvironment());
    }

    private static GameInstallation Installation(string userData)
        => new(
            "project-zomboid:test",
            Path.GetDirectoryName(userData) ?? userData,
            "test",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ProjectZomboidInstallationDiscovery.UserDataPathKey] = userData
            });

    private static EnvironmentManifest EmptyEnvironment()
        => new(
            1,
            "project-zomboid",
            "test-build",
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));

    private static async Task WriteEntryAsync(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name);
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes);
    }

    private static async Task WriteTextEntryAsync(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        await using var writer = new StreamWriter(entry.Open());
        await writer.WriteAsync(text);
    }

    public void Dispose()
    {
        foreach (var workspace in _ownedWorkspaces.ToArray())
        {
            try
            {
                if (Directory.Exists(workspace))
                {
                    ProjectZomboidWorkspaceOwnership.DeleteOwned(workspace);
                }
            }
            catch
            {
            }
        }

        foreach (var package in _packages)
        {
            try
            {
                if (File.Exists(package))
                {
                    File.Delete(package);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

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