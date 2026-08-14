using System.IO.Compression;
using System.Text.Json;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Storage;

namespace SharedWorlds.GameAdapters.Enshrouded;

internal static class EnshroudedWorldState
{
    private const string PreparedSaveDirectoryName = "savegame";
    private const long MinimumFreeSpaceReserveBytes = 128L * 1024 * 1024;

    public static Task<CapturedState> CaptureDetectedWorldAsync(
        DetectedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        return CaptureBundleAsync(
            EnshroudedWorldDiscovery.ResolveFromDataIndexPath(world.SourcePath),
            cancellationToken);
    }

    public static Task<CapturedState> CapturePreparedWorldAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        EnshroudedWorkspaceOwnership.RequireOwned(world);
        return CaptureBundleAsync(
            FindSinglePreparedBundle(world.WorkingDirectory),
            cancellationToken);
    }

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        EnshroudedEnvironment.RequireCompatible(installation, requiredEnvironment);
        return new PreparedWorld(
            installation,
            EnshroudedWorkspaceOwnership.Create(),
            requiredEnvironment,
            DisplayName: null);
    }

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        PreparedWorldPreparationContext preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        EnshroudedEnvironment.RequireCompatible(installation, requiredEnvironment);
        var workspace = Path.GetFullPath(preparation.ManagedWorkingDirectory);
        if (!Directory.Exists(workspace))
        {
            throw new InvalidOperationException(
                "Enshrouded managed workspace must be created by Core before adapter materialization.");
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
        EnshroudedWorkspaceOwnership.RequireOwned(world);

        var packagePath = Path.GetFullPath(state.Path);
        RequireRegularPackage(packagePath);
        using var archive = ZipFile.OpenRead(packagePath);
        var package = InspectPackage(archive, cancellationToken);
        EnsureSufficientFreeSpace(world.WorkingDirectory, package.DeclaredBytes);

        var savePath = Path.Combine(world.WorkingDirectory, PreparedSaveDirectoryName);
        var operationId = Guid.NewGuid().ToString("N");
        var stagingRoot = Path.Combine(
            world.WorkingDirectory,
            $".sharedworlds-staging-{operationId}");
        var stagingSave = Path.Combine(stagingRoot, PreparedSaveDirectoryName);
        var rollbackPath = Path.Combine(
            world.WorkingDirectory,
            $".sharedworlds-rollback-{operationId}");
        var movedExisting = false;

        try
        {
            foreach (var entry in package.Entries)
            {
                await ExtractEntryAsync(
                    entry,
                    Path.Combine(stagingSave, entry.Name),
                    cancellationToken);
            }

            if (Directory.Exists(savePath))
            {
                EnshroudedWorkspaceOwnership.RequireOwnedTree(world, savePath);
                Directory.Move(savePath, rollbackPath);
                movedExisting = true;
            }

            Directory.Move(stagingSave, savePath);
            TryDeleteDirectory(stagingRoot);
            if (movedExisting)
            {
                TryDeleteDirectory(rollbackPath);
            }
        }
        catch (Exception restoreException)
        {
            TryDeleteDirectory(stagingRoot);
            if (movedExisting &&
                !Directory.Exists(savePath) &&
                Directory.Exists(rollbackPath))
            {
                try
                {
                    Directory.Move(rollbackPath, savePath);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Enshrouded state restore failed and the previous prepared World could not be rolled back automatically.",
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
        EnshroudedWorkspaceOwnership.RequireOwned(world);

        switch (disposition)
        {
            case PreparedWorldDisposition.Discard:
                EnshroudedWorkspaceOwnership.DeleteAdapterOwned(world);
                break;
            case PreparedWorldDisposition.ReleaseForCoreManagedDiscard:
                EnshroudedWorkspaceOwnership.RequireManaged(world);
                break;
            case PreparedWorldDisposition.PreserveForRecovery:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null);
        }

        return Task.CompletedTask;
    }

    private static async Task<CapturedState> CaptureBundleAsync(
        EnshroudedNativeBundle bundle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var packagePath = DisposableStatePackageStorage.CreatePackagePath(
            "enshrouded",
            bundle.WorldId,
            ".zip");
        try
        {
            await using var packageStream = new FileStream(
                packagePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true);
            using (var archive = new ZipArchive(
                       packageStream,
                       ZipArchiveMode.Create,
                       leaveOpen: true))
            {
                await WriteEntryAsync(
                    archive,
                    Path.GetFileName(bundle.DataIndexPath),
                    bundle.DataIndexPath,
                    cancellationToken);
                await WriteEntryAsync(
                    archive,
                    Path.GetFileName(bundle.DataPath),
                    bundle.DataPath,
                    cancellationToken);
                await WriteEntryAsync(
                    archive,
                    Path.GetFileName(bundle.InfoIndexPath),
                    bundle.InfoIndexPath,
                    cancellationToken);
                await WriteEntryAsync(
                    archive,
                    Path.GetFileName(bundle.InfoPath),
                    bundle.InfoPath,
                    cancellationToken);
            }

            await packageStream.FlushAsync(cancellationToken);
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

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string entryName,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        RequireRegularNonEmptyFile(sourcePath, $"Enshrouded bundle file '{entryName}'");
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        await using var destination = entry.Open();
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static EnshroudedNativeBundle FindSinglePreparedBundle(string workingDirectory)
    {
        var saveRoot = Path.Combine(workingDirectory, PreparedSaveDirectoryName);
        if (!EnshroudedWorldDiscovery.IsRegularDirectory(saveRoot))
        {
            throw new InvalidOperationException(
                "Prepared Enshrouded workspace does not contain a regular savegame directory.");
        }

        var bundles = EnshroudedWorldDiscovery.KnownWorlds
            .Select(world =>
                EnshroudedWorldDiscovery.TryResolveCurrentBundle(
                    saveRoot,
                    world.Id,
                    out var bundle)
                    ? bundle
                    : null)
            .Where(bundle => bundle is not null)
            .Take(2)
            .ToArray();
        if (bundles.Length != 1)
        {
            throw new InvalidOperationException(
                $"Prepared Enshrouded workspace must contain exactly one current World; found {bundles.Length}.");
        }

        return bundles[0]!;
    }

    private static EnshroudedPackageInspection InspectPackage(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .ToArray();
        if (entries.Length != 4)
        {
            throw new InvalidDataException(
                $"Enshrouded state package must contain exactly four current World files; found {entries.Length}.");
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(entry.Name, entry.FullName, StringComparison.Ordinal) ||
                entry.FullName.Contains('/') ||
                entry.FullName.Contains('\\') ||
                entry.Length <= 0)
            {
                throw new InvalidDataException(
                    $"Enshrouded state package contains an invalid file entry '{entry.FullName}'.");
            }
        }

        var dataIndexes = entries
            .Where(entry =>
                entry.Name.EndsWith("-index", StringComparison.Ordinal) &&
                !entry.Name.EndsWith("_info-index", StringComparison.Ordinal))
            .ToArray();
        if (dataIndexes.Length != 1)
        {
            throw new InvalidDataException(
                "Enshrouded state package must contain exactly one World data index.");
        }

        var dataIndexEntry = dataIndexes[0];
        var worldId = dataIndexEntry.Name[..^"-index".Length];
        if (!EnshroudedWorldDiscovery.IsKnownWorldId(worldId))
        {
            throw new InvalidDataException(
                $"Enshrouded state package contains unsupported World identity '{worldId}'.");
        }

        var infoIndexName = $"{worldId}_info-index";
        var infoIndexEntry = entries.SingleOrDefault(entry =>
            string.Equals(entry.Name, infoIndexName, StringComparison.Ordinal));
        if (infoIndexEntry is null)
        {
            throw new InvalidDataException(
                $"Enshrouded state package is missing '{infoIndexName}'.");
        }

        var dataIndex = ReadIndexEntry(dataIndexEntry, cancellationToken);
        var infoIndex = ReadIndexEntry(infoIndexEntry, cancellationToken);
        if (dataIndex.Deleted || infoIndex.Deleted)
        {
            throw new InvalidDataException(
                "Enshrouded state package indexes mark the World as deleted.");
        }

        var dataName = EnshroudedWorldDiscovery.GetDataFileName(worldId, dataIndex.Latest);
        var infoName = EnshroudedWorldDiscovery.GetInfoFileName(worldId, infoIndex.Latest);
        var dataEntry = entries.SingleOrDefault(entry =>
            string.Equals(entry.Name, dataName, StringComparison.Ordinal));
        var infoEntry = entries.SingleOrDefault(entry =>
            string.Equals(entry.Name, infoName, StringComparison.Ordinal));
        if (dataEntry is null || infoEntry is null)
        {
            throw new InvalidDataException(
                "Enshrouded state package does not contain the active files selected by its indexes.");
        }

        var expectedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            dataIndexEntry.Name,
            dataEntry.Name,
            infoIndexEntry.Name,
            infoEntry.Name
        };
        if (expectedNames.Count != 4 || entries.Any(entry => !expectedNames.Contains(entry.Name)))
        {
            throw new InvalidDataException(
                "Enshrouded state package contains files outside the selected current World revision.");
        }

        long declaredBytes = 0;
        try
        {
            foreach (var entry in entries)
            {
                declaredBytes = checked(declaredBytes + entry.Length);
            }
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "Enshrouded state package declares an impossible extraction size.",
                exception);
        }

        return new EnshroudedPackageInspection(
            worldId,
            entries,
            declaredBytes);
    }

    private static EnshroudedIndexRecord ReadIndexEntry(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        if (entry.Length > EnshroudedWorldDiscovery.MaximumIndexBytes)
        {
            throw new InvalidDataException(
                $"Enshrouded index '{entry.Name}' exceeds Steward's metadata safety limit.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = entry.Open();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("latest", out var latestElement) ||
            !latestElement.TryGetInt32(out var latest) ||
            latest is < 0 or > 9)
        {
            throw new InvalidDataException(
                $"Enshrouded index '{entry.Name}' does not contain a valid latest slot.");
        }

        var deleted = false;
        if (root.TryGetProperty("deleted", out var deletedElement))
        {
            if (deletedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException(
                    $"Enshrouded index '{entry.Name}' has an invalid deleted flag.");
            }

            deleted = deletedElement.GetBoolean();
        }

        return new EnshroudedIndexRecord(latest, deleted);
    }

    private static async Task ExtractEntryAsync(
        ZipArchiveEntry entry,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var source = entry.Open();
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            useAsync: true);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
        if (destination.Length != entry.Length)
        {
            throw new InvalidDataException(
                $"Enshrouded package entry '{entry.Name}' did not extract to its declared length.");
        }
    }

    private static void RequireRegularPackage(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Enshrouded state package must be a .zip file: {path}");
        }

        RequireRegularNonEmptyFile(path, "Enshrouded state package");
    }

    private static void RequireRegularNonEmptyFile(string path, string description)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
                $"{description} is empty and cannot represent a usable Enshrouded World: {path}");
        }
    }

    private static void EnsureSufficientFreeSpace(
        string workingDirectory,
        long declaredBytes)
    {
        long required;
        try
        {
            required = checked(declaredBytes + MinimumFreeSpaceReserveBytes);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "Enshrouded state package declares an impossible file size.",
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
                    $"Enshrouded state restore requires {required} bytes of free space, but only {drive.AvailableFreeSpace} bytes are available.");
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
}

internal sealed record EnshroudedPackageInspection(
    string WorldId,
    IReadOnlyList<ZipArchiveEntry> Entries,
    long DeclaredBytes);

internal static class EnshroudedWorkspaceOwnership
{
    private const string AdapterId = "enshrouded";

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

        RequireRegularManagedTree(world.WorkingDirectory, world.WorkingDirectory);
    }

    public static void RequireOwnedTree(PreparedWorld world, string treeRoot)
    {
        RequireOwned(world);
        if (world.RecoveryLocation is null)
        {
            DisposablePreparedWorkspaceStorage.RequireOwnedTree(
                AdapterId,
                world.WorkingDirectory,
                treeRoot);
            return;
        }

        RequireRegularManagedTree(world.WorkingDirectory, treeRoot);
    }

    public static void DeleteAdapterOwned(PreparedWorld world)
    {
        RequireOwned(world);
        if (world.RecoveryLocation is not null)
        {
            throw new InvalidOperationException(
                "Enshrouded cannot delete a SafeWorld-managed workspace root; Core owns that deletion by durable WorkspaceId.");
        }

        DisposablePreparedWorkspaceStorage.DeleteOwned(
            AdapterId,
            world.WorkingDirectory);
    }

    private static void RequireRegularManagedTree(
        string workingDirectory,
        string treeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var workspace = Path.GetFullPath(workingDirectory);
        var root = Path.GetFullPath(treeRoot);
        var relative = Path.GetRelativePath(workspace, root);
        if (Path.IsPathRooted(relative) ||
            string.Equals(relative, "..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw Refuse(treeRoot);
        }

        if (!Directory.Exists(root))
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            RejectReparsePoint(current, treeRoot);
            foreach (var directory in Directory.EnumerateDirectories(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                RejectReparsePoint(directory, treeRoot);
                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    throw Refuse(treeRoot);
                }
            }
        }
    }

    private static void RejectReparsePoint(string path, string originalPath)
    {
        if (Directory.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw Refuse(originalPath);
        }
    }

    private static InvalidOperationException Refuse(string path)
        => new(
            $"Refusing to use unrecognized or linked Enshrouded SafeWorld workspace '{path}'.");
}
