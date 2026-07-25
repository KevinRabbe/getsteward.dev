using System.IO.Compression;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal static partial class SevenDaysToDieWorldState
{
    public static Task<CapturedState> CaptureDetectedWorldAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(world);
        var userDataRoot = GetRequiredUserDataRoot(installation);
        var identity = GetDetectedWorldIdentity(userDataRoot, world.SourcePath);
        return CaptureWorldBundleAsync(
            userDataRoot,
            identity.WorldName,
            identity.GameName,
            cancellationToken);
    }

    public static Task<CapturedState> CapturePreparedWorldAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        var userDataRoot = Path.GetFullPath(world.WorkingDirectory);
        var identity = ValidateSingleWorldBundle(userDataRoot);
        return CaptureWorldBundleAsync(
            userDataRoot,
            identity.WorldName,
            identity.GameName,
            cancellationToken);
    }

    private static async Task<CapturedState> CaptureWorldBundleAsync(
        string userDataRoot,
        string worldName,
        string gameName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullUserDataRoot = Path.GetFullPath(userDataRoot);
        ValidateWorldBundle(
            fullUserDataRoot,
            worldName,
            gameName,
            requireExclusiveBundle: false);

        var packagePath = Path.Combine(
            Path.GetTempPath(),
            $"sharedworlds-7dtd-{Guid.NewGuid():N}.zip");
        try
        {
            await using var packageStream = new FileStream(
                packagePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true);
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: true);

            var saveRoot = Path.Combine(fullUserDataRoot, "Saves", worldName, gameName);
            await AddDirectoryAsync(
                archive,
                saveRoot,
                Path.Combine("Saves", worldName, gameName),
                cancellationToken);

            var generatedWorldRoot = Path.Combine(fullUserDataRoot, "GeneratedWorlds", worldName);
            if (Directory.Exists(generatedWorldRoot))
            {
                await AddDirectoryAsync(
                    archive,
                    generatedWorldRoot,
                    Path.Combine("GeneratedWorlds", worldName),
                    cancellationToken);
            }

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

    private static async Task AddDirectoryAsync(
        ZipArchive archive,
        string sourceRoot,
        string entryRoot,
        CancellationToken cancellationToken)
    {
        foreach (var filePath in EnumerateCaptureFiles(sourceRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceRoot, filePath);
            var entryPath = Path.Combine(entryRoot, relativePath);
            var entryName = entryPath.Replace(Path.DirectorySeparatorChar, '/');
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
            await using var destinationStream = entry.Open();
            await sourceStream.CopyToAsync(destinationStream, cancellationToken);
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
                RejectLinkedCapturePath(directory);
                pending.Push(directory);
            }

            foreach (var filePath in Directory.EnumerateFiles(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
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
            exception is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException(
                $"Steward could not inspect 7 Days to Die capture path '{path}'.",
                exception);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"7 Days to Die World capture contains a linked or reparse-point path that Steward will not follow: '{path}'.");
        }
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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
