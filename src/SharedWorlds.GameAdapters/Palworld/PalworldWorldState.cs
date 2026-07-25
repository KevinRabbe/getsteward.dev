using System.IO.Compression;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Palworld;

internal static class PalworldWorldState
{
    private const string LevelSaveFileName = "Level.sav";

    public static Task<CapturedState> CaptureDetectedWorldAsync(
        DetectedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        return CaptureWorldDirectoryAsync(world.SourcePath, cancellationToken);
    }

    public static Task<CapturedState> CapturePreparedWorldAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        return CaptureWorldDirectoryAsync(world.WorkingDirectory, cancellationToken);
    }

    public static async Task RestorePreparedWorldAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        var packagePath = Path.GetFullPath(state.Path);
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException(
                "The Palworld state package does not exist.",
                packagePath);
        }

        var destinationPath = Path.GetFullPath(world.WorkingDirectory);
        var parentPath = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException(
                $"Could not determine the parent directory for Palworld world '{destinationPath}'.");
        Directory.CreateDirectory(parentPath);

        var operationId = Guid.NewGuid().ToString("N");
        var stagingPath = destinationPath + ".sharedworlds-staging-" + operationId;
        var rollbackPath = destinationPath + ".sharedworlds-rollback-" + operationId;
        var movedExistingWorld = false;

        try
        {
            await ExtractPackageAsync(packagePath, stagingPath, cancellationToken);

            if (!File.Exists(Path.Combine(stagingPath, LevelSaveFileName)))
            {
                throw new InvalidOperationException(
                    $"The Palworld state package has no root {LevelSaveFileName}.");
            }

            if (Directory.Exists(destinationPath))
            {
                Directory.Move(destinationPath, rollbackPath);
                movedExistingWorld = true;
            }

            Directory.Move(stagingPath, destinationPath);

            if (movedExistingWorld)
            {
                TryDeleteDirectory(rollbackPath);
            }
        }
        catch (Exception restoreException)
        {
            TryDeleteDirectory(stagingPath);

            if (movedExistingWorld &&
                !Directory.Exists(destinationPath) &&
                Directory.Exists(rollbackPath))
            {
                try
                {
                    Directory.Move(rollbackPath, destinationPath);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Palworld state restore failed and the previous world could not be rolled back automatically.",
                        restoreException,
                        rollbackException);
                }
            }

            throw;
        }
    }

    private static async Task<CapturedState> CaptureWorldDirectoryAsync(
        string sourceWorldPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sourcePath = Path.GetFullPath(sourceWorldPath);
        if (!Directory.Exists(sourcePath) ||
            !File.Exists(Path.Combine(sourcePath, LevelSaveFileName)))
        {
            throw new InvalidOperationException(
                $"The Palworld world is not available or has no {LevelSaveFileName}: {sourcePath}");
        }

        var worldId = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath));
        if (string.IsNullOrWhiteSpace(worldId))
        {
            throw new InvalidOperationException(
                $"Could not determine the Palworld world id from: {sourcePath}");
        }

        var packagePath = CreatePackagePath(worldId);
        try
        {
            await CreatePackageAsync(sourcePath, packagePath, cancellationToken);
            return new CapturedState(
                new StatePackage(Path.GetFileNameWithoutExtension(packagePath), packagePath),
                DateTimeOffset.UtcNow);
        }
        catch
        {
            TryDeleteFile(packagePath);
            throw;
        }
    }

    private static async Task CreatePackageAsync(
        string sourcePath,
        string packagePath,
        CancellationToken cancellationToken)
    {
        await using var packageStream = new FileStream(
            packagePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            useAsync: true);
        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var filePath in EnumerateCaptureFiles(sourcePath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourcePath, filePath);
            var entryName = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
            {
                entryName = entryName.Replace(Path.AltDirectorySeparatorChar, '/');
            }

            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            await using var sourceStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                useAsync: true);
            await using var entryStream = entry.Open();
            await sourceStream.CopyToAsync(entryStream, cancellationToken);
        }
    }

    private static IEnumerable<string> EnumerateCaptureFiles(string sourceRoot)
    {
        var fullSourceRoot = Path.GetFullPath(sourceRoot);
        RejectLinkedCapturePath(fullSourceRoot);

        var pending = new Stack<string>();
        pending.Push(fullSourceRoot);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var relativePath = Path.GetRelativePath(fullSourceRoot, directory);
                if (ShouldExclude(relativePath))
                {
                    continue;
                }

                RejectLinkedCapturePath(directory);
                pending.Push(directory);
            }

            foreach (var filePath in Directory.EnumerateFiles(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                var relativePath = Path.GetRelativePath(fullSourceRoot, filePath);
                if (ShouldExclude(relativePath))
                {
                    continue;
                }

                RejectLinkedCapturePath(filePath);
                yield return filePath;
            }
        }
    }

    private static void RejectLinkedCapturePath(string path)
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
                $"Steward could not inspect Palworld capture path '{path}'.",
                exception);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Palworld World capture contains a linked or reparse-point path that Steward will not follow: '{path}'.");
        }
    }

    private static async Task ExtractPackageAsync(
        string packagePath,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingPath);

        var stagingRoot = Path.GetFullPath(stagingPath);
        var stagingPrefix = stagingRoot + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var extractedFiles = new HashSet<string>(pathComparer);

        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(entry.FullName))
            {
                continue;
            }

            var relativePath = entry.FullName
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relativePath))
            {
                throw new InvalidOperationException(
                    $"Palworld state package contains an absolute path: '{entry.FullName}'.");
            }

            var destinationPath = Path.GetFullPath(Path.Combine(stagingRoot, relativePath));
            if (!destinationPath.StartsWith(stagingPrefix, pathComparison))
            {
                throw new InvalidOperationException(
                    $"Palworld state package contains a path outside the world root: '{entry.FullName}'.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            if (!extractedFiles.Add(destinationPath))
            {
                throw new InvalidOperationException(
                    $"Palworld state package contains duplicate file path '{entry.FullName}'.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var sourceStream = entry.Open();
            await using var destinationStream = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true);
            await sourceStream.CopyToAsync(destinationStream, cancellationToken);
            await destinationStream.FlushAsync(cancellationToken);
        }
    }

    private static bool ShouldExclude(string relativePath)
    {
        var segments = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length > 0 &&
            string.Equals(segments[0], "backup", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return segments.Any(segment =>
            segment.Contains(".sharedworlds-backup", StringComparison.OrdinalIgnoreCase) ||
            segment.Contains(".sharedworlds-staging-", StringComparison.OrdinalIgnoreCase) ||
            segment.Contains(".sharedworlds-rollback-", StringComparison.OrdinalIgnoreCase));
    }

    private static string CreatePackagePath(string worldId)
    {
        var root = Path.Combine(GetLocalWorkRoot(), "packages");
        Directory.CreateDirectory(root);

        var safeWorldId = SanitizeFileName(worldId);
        return Path.Combine(root, $"{safeWorldId}-{Guid.NewGuid():N}.zip");
    }

    private static string GetLocalWorkRoot()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(basePath))
        {
            basePath = Path.GetTempPath();
        }

        return Path.Combine(basePath, "SharedWorlds", "palworld");
    }

    private static string SanitizeFileName(string value)
    {
        var result = value;
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(invalidCharacter, '_');
        }

        return string.IsNullOrWhiteSpace(result) ? "world" : result;
    }

    private static void TryDeleteFile(string path)
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
            // Best-effort cleanup after a failed capture.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup after a failed capture.
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
            // Best-effort cleanup. A failed cleanup must not hide the restore result.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup. A failed cleanup must not hide the restore result.
        }
    }
}
