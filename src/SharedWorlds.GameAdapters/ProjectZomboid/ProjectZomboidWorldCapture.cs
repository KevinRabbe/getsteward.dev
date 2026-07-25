using System.IO.Compression;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal static partial class ProjectZomboidWorldState
{
    public static Task<CapturedState> CaptureDetectedWorldAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(world);
        var userDataRoot = GetRequiredUserDataRoot(installation);
        var serverName = GetServerName(world.SourcePath);
        var expectedWorldPath = Path.GetFullPath(Path.Combine(
            userDataRoot,
            "Saves",
            "Multiplayer",
            serverName));
        var actualWorldPath = Path.GetFullPath(world.SourcePath);
        if (!PathsEqual(expectedWorldPath, actualWorldPath))
        {
            throw new InvalidOperationException(
                "The detected Project Zomboid World is not beneath the installation's authoritative multiplayer save root.");
        }

        return CaptureServerBundleAsync(userDataRoot, serverName, cancellationToken);
    }

    public static Task<CapturedState> CapturePreparedWorldAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        var userDataRoot = Path.GetFullPath(world.WorkingDirectory);
        var serverName = ValidateSingleServerBundle(userDataRoot);
        return CaptureServerBundleAsync(userDataRoot, serverName, cancellationToken);
    }

    private static async Task<CapturedState> CaptureServerBundleAsync(
        string userDataRoot,
        string serverName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullUserDataRoot = Path.GetFullPath(userDataRoot);
        ValidateServerBundle(fullUserDataRoot, serverName, requireExclusiveBundle: false);

        var packagePath = Path.Combine(
            Path.GetTempPath(),
            $"sharedworlds-project-zomboid-{Guid.NewGuid():N}.zip");
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

            var worldRoot = Path.Combine(fullUserDataRoot, "Saves", "Multiplayer", serverName);
            foreach (var filePath in EnumerateCaptureFiles(worldRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativeToWorld = Path.GetRelativePath(worldRoot, filePath);
                await AddFileAsync(
                    archive,
                    filePath,
                    Path.Combine("Saves", "Multiplayer", serverName, relativeToWorld),
                    cancellationToken);
            }

            var serverRoot = Path.Combine(fullUserDataRoot, "Server");
            if (Directory.Exists(serverRoot))
            {
                RejectLinkedCapturePath(serverRoot);
            }

            foreach (var suffix in ServerConfigSuffixes)
            {
                var configPath = Path.Combine(serverRoot, serverName + suffix);
                if (File.Exists(configPath))
                {
                    await AddFileAsync(
                        archive,
                        configPath,
                        Path.Combine("Server", serverName + suffix),
                        cancellationToken);
                }
            }

            var databaseRoot = Path.Combine(fullUserDataRoot, "db");
            var databasePath = Path.Combine(databaseRoot, serverName + ".db");
            if (Directory.Exists(databaseRoot))
            {
                RejectLinkedCapturePath(databaseRoot);
            }

            if (File.Exists(databasePath))
            {
                await AddFileAsync(
                    archive,
                    databasePath,
                    Path.Combine("db", serverName + ".db"),
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

    private static async Task AddFileAsync(
        ZipArchive archive,
        string sourcePath,
        string entryPath,
        CancellationToken cancellationToken)
    {
        RejectLinkedCapturePath(sourcePath);

        var entryName = entryPath.Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
        {
            entryName = entryName.Replace(Path.AltDirectorySeparatorChar, '/');
        }

        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var sourceStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        await using var destinationStream = entry.Open();
        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
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
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not inspect Project Zomboid capture path '{path}'.",
                exception);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Project Zomboid World capture contains a linked or reparse-point path that Steward will not follow: '{path}'.");
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
