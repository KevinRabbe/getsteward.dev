using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Storage;

namespace SharedWorlds.GameAdapters.Necesse;

internal static class NecesseWorldState
{
    private const string PreparedWorldRelativePath = "saves/worlds/world.zip";
    private const long MinimumFreeSpaceReserveBytes = 128L * 1024 * 1024;

    public static Task<CapturedState> CaptureDetectedWorldAsync(
        DetectedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        return CaptureWorldFileAsync(
            world.SourcePath,
            Path.GetFileNameWithoutExtension(world.SourcePath),
            cancellationToken);
    }

    public static Task<CapturedState> CapturePreparedWorldAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        NecesseWorkspaceOwnership.RequireOwned(world.WorkingDirectory);
        return CaptureWorldFileAsync(
            GetPreparedWorldPath(world.WorkingDirectory),
            world.DisplayName ?? "world",
            cancellationToken);
    }

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        NecesseEnvironment.RequireCompatible(installation, requiredEnvironment);
        return new PreparedWorld(
            installation,
            NecesseWorkspaceOwnership.Create(),
            requiredEnvironment,
            DisplayName: null);
    }

    public static async Task RestorePreparedWorldAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        NecesseWorkspaceOwnership.RequireOwned(world.WorkingDirectory);

        var sourcePath = Path.GetFullPath(state.Path);
        RequireRegularZip(sourcePath, "Necesse state package");
        EnsureSufficientFreeSpace(
            world.WorkingDirectory,
            new FileInfo(sourcePath).Length);

        var destinationPath = GetPreparedWorldPath(world.WorkingDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var operationId = Guid.NewGuid().ToString("N");
        var stagingPath = destinationPath + ".sharedworlds-staging-" + operationId;
        var rollbackPath = destinationPath + ".sharedworlds-rollback-" + operationId;
        var movedExisting = false;

        try
        {
            await CopyFileAsync(sourcePath, stagingPath, cancellationToken);
            if (File.Exists(destinationPath))
            {
                RequireRegularZip(
                    destinationPath,
                    "existing prepared Necesse World");
                File.Move(destinationPath, rollbackPath);
                movedExisting = true;
            }

            File.Move(stagingPath, destinationPath);
            if (movedExisting)
            {
                TryDeleteFile(rollbackPath);
            }
        }
        catch (Exception restoreException)
        {
            TryDeleteFile(stagingPath);
            if (movedExisting &&
                !File.Exists(destinationPath) &&
                File.Exists(rollbackPath))
            {
                try
                {
                    File.Move(rollbackPath, destinationPath);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Necesse state restore failed and the previous prepared World could not be rolled back automatically.",
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
        NecesseWorkspaceOwnership.RequireOwned(world.WorkingDirectory);

        if (disposition == PreparedWorldDisposition.Discard)
        {
            NecesseWorkspaceOwnership.DeleteOwned(world.WorkingDirectory);
        }

        return Task.CompletedTask;
    }

    private static async Task<CapturedState> CaptureWorldFileAsync(
        string sourcePath,
        string worldName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullSourcePath = Path.GetFullPath(sourcePath);
        RequireRegularZip(fullSourcePath, "Necesse World");

        var packagePath = DisposableStatePackageStorage.CreatePackagePath(
            "necesse",
            worldName,
            ".zip");
        try
        {
            await CopyFileAsync(fullSourcePath, packagePath, cancellationToken);
            return new CapturedState(
                new StatePackage(
                    Path.GetFileNameWithoutExtension(packagePath),
                    packagePath),
                DateTimeOffset.UtcNow);
        }
        catch
        {
            TryDeleteFile(packagePath);
            throw;
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            useAsync: true);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    private static string GetPreparedWorldPath(string workingDirectory)
        => Path.GetFullPath(Path.Combine(
            workingDirectory,
            PreparedWorldRelativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static void RequireRegularZip(string path, string description)
    {
        if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{description} must be a .zip file: {path}");
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or
                DirectoryNotFoundException or
                IOException or
                UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"{description} could not be inspected safely: {path}",
                exception);
        }

        if ((attributes & FileAttributes.Directory) != 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"{description} must be a regular non-linked file: {path}");
        }
    }

    private static void EnsureSufficientFreeSpace(
        string workingDirectory,
        long packageBytes)
    {
        long required;
        try
        {
            required = checked(packageBytes + MinimumFreeSpaceReserveBytes);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "Necesse state package declares an impossible file size.",
                exception);
        }

        var root = Path.GetPathRoot(Path.GetFullPath(workingDirectory));
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        try
        {
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < required)
            {
                throw new IOException(
                    $"Necesse state restore requires {required} bytes of free space, but only {drive.AvailableFreeSpace} bytes are available.");
            }
        }
        catch (DriveNotFoundException)
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

internal static class NecesseWorkspaceOwnership
{
    private const string AdapterId = "necesse";

    public static string Create()
        => DisposablePreparedWorkspaceStorage.Create(AdapterId);

    public static void RequireOwned(string workingDirectory)
        => _ = DisposablePreparedWorkspaceStorage.RequireOwned(
            AdapterId,
            workingDirectory);

    public static void DeleteOwned(string workingDirectory)
        => DisposablePreparedWorkspaceStorage.DeleteOwned(
            AdapterId,
            workingDirectory);
}
