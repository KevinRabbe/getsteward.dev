using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Storage;

namespace SharedWorlds.GameAdapters.PlanetCrafter;

internal static class PlanetCrafterWorldState
{
    private const string PreparedSaveDirectoryName = "save";
    private const string PreparedWorldFileName = "world.json";
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
        PlanetCrafterWorkspaceOwnership.RequireOwned(world);
        return CaptureFileAsync(
            GetPreparedWorldPath(world.WorkingDirectory),
            world.DisplayName ?? "world",
            cancellationToken);
    }

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        PlanetCrafterEnvironment.RequireCompatible(installation, requiredEnvironment);
        return new PreparedWorld(
            installation,
            PlanetCrafterWorkspaceOwnership.Create(),
            requiredEnvironment,
            DisplayName: null);
    }

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        PreparedWorldPreparationContext preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        PlanetCrafterEnvironment.RequireCompatible(installation, requiredEnvironment);
        var workspace = Path.GetFullPath(preparation.ManagedWorkingDirectory);
        if (!Directory.Exists(workspace))
        {
            throw new InvalidOperationException(
                "Planet Crafter managed workspace must be created by Core before adapter materialization.");
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
        PlanetCrafterWorkspaceOwnership.RequireOwned(world);

        var sourcePath = Path.GetFullPath(state.Path);
        RequireRegularWorldFile(sourcePath, "Planet Crafter state package");
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
                    "existing prepared Planet Crafter World");
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
                        "Planet Crafter state restore failed and the previous prepared World could not be rolled back automatically.",
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
        PlanetCrafterWorkspaceOwnership.RequireOwned(world);

        switch (disposition)
        {
            case PreparedWorldDisposition.Discard:
                PlanetCrafterWorkspaceOwnership.DeleteAdapterOwned(world);
                break;
            case PreparedWorldDisposition.ReleaseForCoreManagedDiscard:
                PlanetCrafterWorkspaceOwnership.RequireManaged(world);
                break;
            case PreparedWorldDisposition.PreserveForRecovery:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null);
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
        RequireRegularWorldFile(fullSourcePath, "Planet Crafter World");

        var packagePath = DisposableStatePackageStorage.CreatePackagePath(
            "planet-crafter",
            worldName,
            ".json");
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
                ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{description} must be a .json file: {path}");
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
                $"{description} is empty and cannot represent a usable Planet Crafter World: {path}");
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
                "Planet Crafter state package declares an impossible file size.",
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
                    $"Planet Crafter state restore requires {required} bytes of free space, but only {drive.AvailableFreeSpace} bytes are available.");
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

internal static class PlanetCrafterWorkspaceOwnership
{
    private const string AdapterId = "planet-crafter";

    public static string Create()
        => DisposablePreparedWorkspaceStorage.Create(AdapterId);

    public static void RequireOwned(PreparedWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.RecoveryLocation is null)
        {
            _ = DisposablePreparedWorkspaceStorage.RequireOwned(
                AdapterId,
                world.WorkingDirectory);
            return;
        }

        RequireManaged(world);
    }

    public static void RequireManaged(PreparedWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var location = world.RecoveryLocation
            ?? throw Refuse(world.WorkingDirectory);
        location.Validate();
        if (location.Kind != PreparedWorldRecoveryLocationKind.SafeWorldManaged)
        {
            throw Refuse(world.WorkingDirectory);
        }

        RequireRegularManagedTree(world.WorkingDirectory);
    }

    public static void DeleteAdapterOwned(PreparedWorld world)
    {
        RequireOwned(world);
        if (world.RecoveryLocation is not null)
        {
            throw new InvalidOperationException(
                "Planet Crafter cannot delete a SafeWorld-managed workspace root; Core owns that deletion by durable WorkspaceId.");
        }

        DisposablePreparedWorkspaceStorage.DeleteOwned(
            AdapterId,
            world.WorkingDirectory);
    }

    private static void RequireRegularManagedTree(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(workingDirectory));
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

    private static void RejectReparsePoint(
        string path,
        string originalPath)
    {
        if (Directory.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw Refuse(originalPath);
        }
    }

    private static InvalidOperationException Refuse(string path)
        => new(
            $"Refusing to use unrecognized or linked Planet Crafter SafeWorld workspace '{path}'.");
}
