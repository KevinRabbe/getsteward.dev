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

        foreach (var filePath in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourcePath, filePath);
            if (ShouldExclude(relativePath))
            {
                continue;
            }

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

    private static bool ShouldExclude(string relativePath)
    {
        return relativePath
            .Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment =>
                string.Equals(segment, "backup", StringComparison.OrdinalIgnoreCase) ||
                segment.Contains(".sharedworlds-backup", StringComparison.OrdinalIgnoreCase) ||
                segment.Contains(".sharedworlds-staging-", StringComparison.OrdinalIgnoreCase));
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
}
