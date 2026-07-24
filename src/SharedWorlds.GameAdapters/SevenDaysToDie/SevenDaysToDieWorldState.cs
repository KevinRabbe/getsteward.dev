using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal static class SevenDaysToDieWorldState
{
    private const string MainWorldFileName = "main.ttw";

    public static Task<CapturedState> CaptureDetectedWorldAsync(
        DetectedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        return CaptureDirectoryAsync(world.SourcePath, cancellationToken);
    }

    public static Task<CapturedState> CapturePreparedWorldAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        return CaptureDirectoryAsync(world.WorkingDirectory, cancellationToken);
    }

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(requiredEnvironment);

        var verification = SevenDaysToDieEnvironment.Verify(installation, requiredEnvironment);
        if (!verification.IsReady)
        {
            throw new EnvironmentReproductionException(
                "7-days-to-die",
                string.Join("; ", verification.Issues.Select(issue => issue.Message)));
        }

        var workingDirectory = Path.Combine(
            GetLocalWorkRoot(),
            Guid.NewGuid().ToString("N"),
            "world");
        Directory.CreateDirectory(Path.GetDirectoryName(workingDirectory)!);
        return new PreparedWorld(
            installation,
            workingDirectory,
            requiredEnvironment);
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
                "The 7 Days to Die state package does not exist.",
                packagePath);
        }

        var destinationPath = Path.GetFullPath(world.WorkingDirectory);
        var parentPath = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException(
                $"Could not determine the parent directory for 7 Days to Die World '{destinationPath}'.");
        Directory.CreateDirectory(parentPath);

        var operationId = Guid.NewGuid().ToString("N");
        var stagingPath = destinationPath + ".sharedworlds-staging-" + operationId;
        var rollbackPath = destinationPath + ".sharedworlds-rollback-" + operationId;
        var movedExistingWorld = false;

        try
        {
            await ExtractPackageAsync(packagePath, stagingPath, cancellationToken);
            if (!File.Exists(Path.Combine(stagingPath, MainWorldFileName)))
            {
                throw new InvalidOperationException(
                    $"The 7 Days to Die state package has no root {MainWorldFileName}.");
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
                        "7 Days to Die state restore failed and the previous prepared World could not be rolled back automatically.",
                        restoreException,
                        rollbackException);
                }
            }

            throw;
        }
    }

    public static Task FinalizePreparedWorldAsync(
        PreparedWorld world,
        PreparedWorldDisposition disposition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        cancellationToken.ThrowIfCancellationRequested();
        if (disposition == PreparedWorldDisposition.PreserveForRecovery)
        {
            return Task.CompletedTask;
        }

        var workingDirectory = Path.GetFullPath(world.WorkingDirectory);
        var operationRoot = Directory.GetParent(workingDirectory)?.FullName;
        if (operationRoot is not null)
        {
            TryDeleteDirectory(operationRoot);
        }

        return Task.CompletedTask;
    }

    private static async Task<CapturedState> CaptureDirectoryAsync(
        string sourceWorldPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourcePath = Path.GetFullPath(sourceWorldPath);
        if (!Directory.Exists(sourcePath) ||
            !File.Exists(Path.Combine(sourcePath, MainWorldFileName)))
        {
            throw new InvalidOperationException(
                $"The 7 Days to Die World is unavailable or has no root {MainWorldFileName}: {sourcePath}");
        }

        var packagePath = Path.Combine(
            Path.GetTempPath(),
            $"sharedworlds-7dtd-{Guid.NewGuid():N}.zip");
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
            await using var destinationStream = entry.Open();
            await sourceStream.CopyToAsync(destinationStream, cancellationToken);
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
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var extractedFiles = new HashSet<string>(comparer);

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
                    $"7 Days to Die state package contains an absolute path: '{entry.FullName}'.");
            }

            var destinationPath = Path.GetFullPath(Path.Combine(stagingRoot, relativePath));
            if (!destinationPath.StartsWith(stagingPrefix, comparison))
            {
                throw new InvalidOperationException(
                    $"7 Days to Die state package contains a path outside the World root: '{entry.FullName}'.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            if (!extractedFiles.Add(destinationPath))
            {
                throw new InvalidOperationException(
                    $"7 Days to Die state package contains duplicate file path '{entry.FullName}'.");
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

    private static string GetLocalWorkRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.GetTempPath();
        }

        var root = Path.Combine(localData, "Steward", "workspaces", "7-days-to-die");
        Directory.CreateDirectory(root);
        return root;
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
