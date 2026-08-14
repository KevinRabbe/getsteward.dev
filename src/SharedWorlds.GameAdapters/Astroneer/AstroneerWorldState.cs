using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Storage;

namespace SharedWorlds.GameAdapters.Astroneer;

internal static class AstroneerWorldState
{
    private const string PreparedSaveDirectoryName = "save";
    private const string PreparedWorldFileName = "world.savegame";
    private const long MinimumFreeSpaceReserveBytes = 128L * 1024 * 1024;

    public static Task<CapturedState> CaptureDetectedWorldAsync(
        DetectedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        return CaptureFileAsync(
            world.SourcePath,
            Path.GetFileNameWithoutExtension(world.SourcePath),
            cancellationToken);
    }

    public static Task<CapturedState> CapturePreparedWorldAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        AstroneerWorkspaceOwnership.RequireOwned(world);
        return CaptureFileAsync(
            GetPreparedWorldPath(world.WorkingDirectory),
            world.DisplayName ?? "world",
            cancellationToken);
    }

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        AstroneerEnvironment.RequireCompatible(installation, requiredEnvironment);
        return new PreparedWorld(
            installation,
            AstroneerWorkspaceOwnership.Create(),
            requiredEnvironment,
            DisplayName: null);
    }

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        PreparedWorldPreparationContext preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        AstroneerEnvironment.RequireCompatible(installation, requiredEnvironment);
        var workspace = Path.GetFullPath(preparation.ManagedWorkingDirectory);
        if (!Directory.Exists(workspace))
        {
            throw new InvalidOperationException(
                "ASTRONEER managed workspace must be created by Core before adapter materialization.");
        }

        return new PreparedWorld(
            installation,
            workspace,
            requiredEnvironment,
            DisplayName: null,
            RecoveryLocation: PreparedWorldRecoveryLocation.Managed());
    }

    public static async Task RestorePreparedWorldAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        AstroneerWorkspaceOwnership.RequireOwned(world);

        var sourcePath = Path.GetFullPath(state.Path);
        RequireRegularWorldFile(sourcePath, "ASTRONEER state package");
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
                RequireRegularWorldFile(
                    destinationPath,
                    "existing prepared ASTRONEER World");
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
                        "ASTRONEER state restore failed and the previous prepared World could not be rolled back automatically.",
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
        AstroneerWorkspaceOwnership.RequireOwned(world);

        if (disposition == PreparedWorldDisposition.Discard)
        {
            AstroneerWorkspaceOwnership.DeleteOwned(world);
        }

        return Task.CompletedTask;
    }

    private static async Task<CapturedState> CaptureFileAsync(
        string sourcePath,
        string worldName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullSourcePath = Path.GetFullPath(sourcePath);
        RequireRegularWorldFile(fullSourcePath, "ASTRONEER World");

        var packagePath = DisposableStatePackageStorage.CreatePackagePath(
            "astroneer",
            worldName,
            ".savegame");
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
            PreparedSaveDirectoryName,
            PreparedWorldFileName));

    private static void RequireRegularWorldFile(
        string path,
        string description)
    {
        if (!string.Equals(
                Path.GetExtension(path),
                ".savegame",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{description} must be a .savegame file: {path}");
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
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

        if (new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException(
                $"{description} is empty and cannot represent a usable ASTRONEER World: {path}");
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
                "ASTRONEER state package declares an impossible file size.",
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
                    $"ASTRONEER state restore requires {required} bytes of free space, but only {drive.AvailableFreeSpace} bytes are available.");
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

internal static class AstroneerWorkspaceOwnership
{
    private const string AdapterId = "astroneer";

    public static string Create()
        => DisposablePreparedWorkspaceStorage.Create(AdapterId);

    public static void RequireOwned(PreparedWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.RecoveryLocation is not { } location)
        {
            _ = DisposablePreparedWorkspaceStorage.RequireOwned(
                AdapterId,
                world.WorkingDirectory);
            return;
        }

        location.Validate();
        if (location.Kind != PreparedWorldRecoveryLocationKind.SafeWorldManaged)
        {
            throw Refuse(world.WorkingDirectory);
        }

        RequireRegularManagedTree(world.WorkingDirectory);
    }

    public static void DeleteOwned(PreparedWorld world)
    {
        RequireOwned(world);
        if (!Directory.Exists(world.WorkingDirectory))
        {
            return;
        }

        if (world.RecoveryLocation is null)
        {
            DisposablePreparedWorkspaceStorage.DeleteOwned(
                AdapterId,
                world.WorkingDirectory);
            return;
        }

        // Transitional compatibility until all normal lifecycle finalization is routed through the
        // Core PreparedWorldFinalizationCoordinator.
        RequireRegularManagedTree(world.WorkingDirectory);
        Directory.Delete(world.WorkingDirectory, recursive: true);
    }

    private static void RequireRegularManagedTree(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var root = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(root))
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            RejectReparsePoint(current, workingDirectory);
            foreach (var directory in Directory.EnumerateDirectories(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                RejectReparsePoint(directory, workingDirectory);
                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    throw Refuse(workingDirectory);
                }
            }
        }
    }

    private static void RejectReparsePoint(string path, string originalPath)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw Refuse(originalPath);
        }
    }

    private static InvalidOperationException Refuse(string path)
        => new(
            $"Refusing to use unrecognized or linked ASTRONEER Safe World workspace '{path}'.");
}
