using System.IO.Compression;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Palworld.Tests;

public sealed class PalworldNativeBackupCaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-palworld-native-backup-{Guid.NewGuid():N}");
    private string? _packagePath;

    [Fact]
    public async Task CaptureExcludesTopLevelNativeBackupButKeepsDeeperBackupNamedPath()
    {
        var worldRoot = Path.Combine(_root, "world");
        Directory.CreateDirectory(worldRoot);
        File.WriteAllBytes(Path.Combine(worldRoot, "Level.sav"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(worldRoot, "LevelMeta.sav"), [4, 5, 6]);

        var playersRoot = Path.Combine(worldRoot, "Players");
        Directory.CreateDirectory(playersRoot);
        File.WriteAllBytes(Path.Combine(playersRoot, "player.sav"), [7, 8, 9]);

        var nativeBackupRoot = Path.Combine(worldRoot, "backup", "world", "2026.07.25-12.00.00");
        Directory.CreateDirectory(nativeBackupRoot);
        File.WriteAllBytes(Path.Combine(nativeBackupRoot, "Level.sav"), [10, 11, 12]);

        var deeperBackupNamedRoot = Path.Combine(worldRoot, "custom", "backup");
        Directory.CreateDirectory(deeperBackupNamedRoot);
        File.WriteAllText(Path.Combine(deeperBackupNamedRoot, "keep.txt"), "authoritative-extra-data");

        var adapter = new PalworldAdapter();
        var installation = new GameInstallation("palworld:test", _root, "test");
        var world = new DetectedWorld(worldRoot, "Test World", worldRoot);

        var captured = await adapter.CaptureDetectedWorldAsync(installation, world);
        _packagePath = captured.Package.Path;

        using var archive = ZipFile.OpenRead(captured.Package.Path);
        var entryNames = archive.Entries
            .Select(entry => entry.FullName)
            .ToArray();

        Assert.Contains("Level.sav", entryNames);
        Assert.Contains("LevelMeta.sav", entryNames);
        Assert.Contains("Players/player.sav", entryNames);
        Assert.Contains("custom/backup/keep.txt", entryNames);
        Assert.DoesNotContain(entryNames, entryName =>
            entryName.StartsWith("backup/", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        TryDeleteFile(_packagePath);
        TryDeleteDirectory(_root);
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
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
