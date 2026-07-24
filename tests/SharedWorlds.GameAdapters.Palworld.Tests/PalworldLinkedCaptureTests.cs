using System.IO.Compression;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Palworld.Tests;

public sealed class PalworldLinkedCaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-palworld-linked-capture-{Guid.NewGuid():N}");

    [Fact]
    public async Task CaptureRejectsLinkedDirectoryInsteadOfFollowingOutsideWorld()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var worldPath = CreateWorld("linked-world");
        var outside = Path.Combine(_root, "outside-world");
        Directory.CreateDirectory(outside);
        await File.WriteAllBytesAsync(Path.Combine(outside, "outside.sav"), [9, 9, 9]);

        var linkedDirectory = Path.Combine(worldPath, "Players", "LinkedOutside");
        Directory.CreateDirectory(Path.GetDirectoryName(linkedDirectory)!);
        Directory.CreateSymbolicLink(linkedDirectory, outside);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PalworldWorldState.CaptureDetectedWorldAsync(
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
    public async Task CaptureSkipsExcludedBackupLinkWithoutFollowingIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var worldPath = CreateWorld("backup-link");
        var outside = Path.Combine(_root, "outside-backup");
        Directory.CreateDirectory(outside);
        await File.WriteAllBytesAsync(Path.Combine(outside, "outside.sav"), [7, 8, 9]);

        var backupLink = Path.Combine(worldPath, "backup");
        Directory.CreateSymbolicLink(backupLink, outside);
        CapturedState? captured = null;
        try
        {
            captured = await PalworldWorldState.CaptureDetectedWorldAsync(
                new DetectedWorld(worldPath, "backup-link", worldPath),
                CancellationToken.None);

            using var archive = ZipFile.OpenRead(captured.Package.Path);
            Assert.Contains(archive.Entries, entry => entry.FullName == "Level.sav");
            Assert.DoesNotContain(
                archive.Entries,
                entry => entry.FullName.Contains("outside.sav", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(backupLink);
            if (captured is not null && File.Exists(captured.Package.Path))
            {
                File.Delete(captured.Package.Path);
            }
        }
    }

    private string CreateWorld(string worldName)
    {
        var worldPath = Path.Combine(_root, worldName);
        Directory.CreateDirectory(worldPath);
        File.WriteAllBytes(Path.Combine(worldPath, "Level.sav"), [1, 2, 3]);
        return worldPath;
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
