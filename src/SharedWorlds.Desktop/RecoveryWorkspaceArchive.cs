using System.IO;
using System.IO.Compression;

namespace SharedWorlds.Desktop;

internal static class RecoveryWorkspaceArchive
{
    public static void Create(string sourceDirectory, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var sourceRoot = Path.GetFullPath(sourceDirectory);
        var archivePath = Path.GetFullPath(destinationPath);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException(
                $"Recovery workspace does not exist: {sourceRoot}");
        }

        if (IsPathInsideDirectory(archivePath, sourceRoot))
        {
            throw new InvalidOperationException(
                "Recovery archive destination must be outside the preserved workspace.");
        }

        RejectLinkedPath(sourceRoot);
        var archiveDirectory = Path.GetDirectoryName(archivePath)
            ?? throw new InvalidOperationException("Could not resolve recovery archive destination directory.");
        Directory.CreateDirectory(archiveDirectory);

        try
        {
            using var output = new FileStream(
                archivePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);

            var pending = new Stack<string>();
            pending.Push(sourceRoot);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                foreach (var directory in Directory.EnumerateDirectories(
                             current,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    RejectLinkedPath(directory);
                    var relativeDirectory = Path.GetRelativePath(sourceRoot, directory);
                    archive.CreateEntry(ToZipEntryName(relativeDirectory) + "/");
                    pending.Push(directory);
                }

                foreach (var filePath in Directory.EnumerateFiles(
                             current,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    RejectLinkedPath(filePath);
                    var relativeFile = Path.GetRelativePath(sourceRoot, filePath);
                    var entry = archive.CreateEntry(
                        ToZipEntryName(relativeFile),
                        CompressionLevel.Optimal);
                    using var source = new FileStream(
                        filePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 128 * 1024,
                        useAsync: false);
                    using var destination = entry.Open();
                    source.CopyTo(destination, 128 * 1024);
                }
            }
        }
        catch
        {
            TryDelete(archivePath);
            throw;
        }
    }

    private static void RejectLinkedPath(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not inspect recovery workspace path '{path}'.",
                exception);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Recovery workspace contains a linked or reparse-point path that Steward will not export: '{path}'.");
        }
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) +
                                  Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return normalizedPath.StartsWith(normalizedDirectory, comparison);
    }

    private static string ToZipEntryName(string relativePath)
    {
        var entryName = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
        {
            entryName = entryName.Replace(Path.AltDirectorySeparatorChar, '/');
        }

        return entryName;
    }

    private static void TryDelete(string path)
    {
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
}
