using System.IO.Compression;
using Xunit;

namespace SharedWorlds.Desktop.AcceptanceTests;

public sealed class RecoveryWorkspaceArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"steward-recovery-archive-{Guid.NewGuid():N}");

    [Fact]
    public void CreateArchivesRegularFilesAndEmptyDirectories()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(Path.Combine(source, "nested", "empty"));
        File.WriteAllBytes(Path.Combine(source, "root.bin"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(source, "nested", "state.txt"), "state");
        var destination = Path.Combine(_root, "recovery.zip");

        RecoveryWorkspaceArchive.Create(source, destination);

        using var archive = ZipFile.OpenRead(destination);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("root.bin", names);
        Assert.Contains("nested/", names);
        Assert.Contains("nested/empty/", names);
        Assert.Contains("nested/state.txt", names);
        Assert.Equal(
            "state",
            ReadText(archive.GetEntry("nested/state.txt")!));
    }

    [Fact]
    public void CreateRejectsDestinationInsideWorkspace()
    {
        var source = Path.Combine(_root, "inside-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "state.txt"), "state");
        var destination = Path.Combine(source, "recovery.zip");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            RecoveryWorkspaceArchive.Create(source, destination));

        Assert.Contains("outside the preserved workspace", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void CreateRejectsLinkedDirectoryAndDeletesPartialArchive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = Path.Combine(_root, "linked-directory-source");
        var outside = Path.Combine(_root, "outside-directory");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(source, "ordinary.txt"), "ordinary");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "outside");

        var linkedDirectory = Path.Combine(source, "linked");
        Directory.CreateSymbolicLink(linkedDirectory, outside);
        var destination = Path.Combine(_root, "linked-directory.zip");
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                RecoveryWorkspaceArchive.Create(source, destination));

            Assert.Contains("linked or reparse-point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            Directory.Delete(linkedDirectory);
        }
    }

    [Fact]
    public void CreateRejectsLinkedFileAndDeletesPartialArchive()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = Path.Combine(_root, "linked-file-source");
        Directory.CreateDirectory(source);
        var outside = Path.Combine(_root, "outside-file.txt");
        File.WriteAllText(outside, "outside");

        var linkedFile = Path.Combine(source, "linked.txt");
        File.CreateSymbolicLink(linkedFile, outside);
        var destination = Path.Combine(_root, "linked-file.zip");
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                RecoveryWorkspaceArchive.Create(source, destination));

            Assert.Contains("linked or reparse-point", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            File.Delete(linkedFile);
        }
    }

    private static string ReadText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
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
