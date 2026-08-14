using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Storage;

namespace SharedWorlds.GameAdapters.AbioticFactor;

internal static class AbioticFactorWorldState
{
    private const string PreparedWorldRelativePath = "save/world";
    private const int MaximumPackageEntries = 50_000;
    private const long MaximumUncompressedBytes = 16L * 1024 * 1024 * 1024;
    private const long MinimumFreeSpaceReserveBytes = 128L * 1024 * 1024;

    public static Task<CapturedState> CaptureDetectedWorldAsync(
        DetectedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        return CaptureDirectoryAsync(
            world.SourcePath,
            world.DisplayName,
            cancellationToken);
    }

    public static Task<CapturedState> CapturePreparedWorldAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        AbioticFactorWorkspaceOwnership.RequireOwned(world);
        return CaptureDirectoryAsync(
            GetPreparedWorldPath(world.WorkingDirectory),
            world.DisplayName ?? "world",
            cancellationToken);
    }

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        AbioticFactorEnvironment.RequireCompatible(installation, requiredEnvironment);
        return new PreparedWorld(
            installation,
            AbioticFactorWorkspaceOwnership.Create(),
            requiredEnvironment,
            DisplayName: null);
    }

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment,
        PreparedWorldPreparationContext preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        AbioticFactorEnvironment.RequireCompatible(installation, requiredEnvironment);
        var workspace = Path.GetFullPath(preparation.ManagedWorkingDirectory);
        if (!Directory.Exists(workspace))
        {
            throw new InvalidOperationException(
                "Abiotic Factor managed workspace must be created by Core before adapter materialization.");
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
        AbioticFactorWorkspaceOwnership.RequireOwned(world);

        var packagePath = Path.GetFullPath(state.Path);
        RequireRegularPackageFile(packagePath);

        using var archive = ZipFile.OpenRead(packagePath);
        var package = InspectPackage(archive, cancellationToken);
        EnsureSufficientFreeSpace(world.WorkingDirectory, package.TotalBytes);

        var destinationPath = GetPreparedWorldPath(world.WorkingDirectory);
        var operationId = Guid.NewGuid().ToString("N");
        var stagingPath = Path.Combine(
            world.WorkingDirectory,
            $".sharedworlds-staging-{operationId}");
        var rollbackPath = Path.Combine(
            world.WorkingDirectory,
            $".sharedworlds-rollback-{operationId}");
        var movedExisting = false;

        try
        {
            Directory.CreateDirectory(stagingPath);
            foreach (var entry in package.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = GetSafeDestinationPath(stagingPath, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var source = entry.Open();
                await using var output = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    useAsync: true);
                await source.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                if (output.Length != entry.Length)
                {
                    throw new InvalidDataException(
                        $"Abiotic Factor package entry '{entry.FullName}' did not extract to the declared byte length.");
                }
            }

            if (!AbioticFactorWorldDiscovery.TryInspectWorldTree(stagingPath))
            {
                throw new InvalidDataException(
                    "The restored Abiotic Factor package does not contain a usable regular World tree.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (Directory.Exists(destinationPath))
            {
                AbioticFactorWorkspaceOwnership.RequireOwnedTree(
                    world,
                    destinationPath);
                Directory.Move(destinationPath, rollbackPath);
                movedExisting = true;
            }

            Directory.Move(stagingPath, destinationPath);
            if (movedExisting)
            {
                TryDeleteDirectory(rollbackPath);
            }
        }
        catch (Exception restoreException)
        {
            TryDeleteDirectory(stagingPath);
            if (movedExisting &&
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
                        "Abiotic Factor state restore failed and the previous prepared World could not be rolled back automatically.",
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
        AbioticFactorWorkspaceOwnership.RequireOwned(world);

        switch (disposition)
        {
            case PreparedWorldDisposition.Discard:
                AbioticFactorWorkspaceOwnership.DeleteAdapterOwned(world);
                break;
            case PreparedWorldDisposition.ReleaseForCoreManagedDiscard:
                AbioticFactorWorkspaceOwnership.RequireManaged(world);
                break;
            case PreparedWorldDisposition.PreserveForRecovery:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null);
        }

        return Task.CompletedTask;
    }

    private static async Task<CapturedState> CaptureDirectoryAsync(
        string sourcePath,
        string worldName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(sourcePath);
        var files = EnumerateWorldFiles(root, cancellationToken);
        var packagePath = DisposableStatePackageStorage.CreatePackagePath(
            "abiotic-factor",
            worldName,
            ".zip");

        try
        {
            await using var packageStream = new FileStream(
                packagePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                useAsync: true);
            using (var archive = new ZipArchive(
                       packageStream,
                       ZipArchiveMode.Create,
                       leaveOpen: true))
            {
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!AbioticFactorWorldDiscovery.IsRegularFile(file.FullPath))
                    {
                        throw new InvalidOperationException(
                            $"Abiotic Factor World file became linked or unreadable before capture: {file.FullPath}");
                    }

                    var entry = archive.CreateEntry(
                        file.RelativePath,
                        CompressionLevel.Fastest);
                    await using var source = new FileStream(
                        file.FullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        128 * 1024,
                        useAsync: true);
                    await using var destination = entry.Open();
                    await source.CopyToAsync(destination, cancellationToken);
                }
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

    private static IReadOnlyList<WorldFile> EnumerateWorldFiles(
        string root,
        CancellationToken cancellationToken)
    {
        if (!AbioticFactorWorldDiscovery.TryInspectWorldTree(root))
        {
            throw new InvalidOperationException(
                $"Abiotic Factor World must be a usable regular non-linked directory tree: {root}");
        }

        var result = new List<WorldFile>();
        var pending = new Stack<string>();
        pending.Push(root);
        long totalBytes = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (!AbioticFactorWorldDiscovery.IsRegularDirectory(directory))
                {
                    throw new InvalidOperationException(
                        $"Abiotic Factor World contains a linked or unreadable directory: {directory}");
                }

                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (!AbioticFactorWorldDiscovery.IsRegularFile(file))
                {
                    throw new InvalidOperationException(
                        $"Abiotic Factor World contains a linked or unreadable file: {file}");
                }

                if (result.Count >= MaximumPackageEntries)
                {
                    throw new InvalidDataException(
                        $"Abiotic Factor World exceeds Steward's {MaximumPackageEntries}-file package limit.");
                }

                var length = new FileInfo(file).Length;
                try
                {
                    totalBytes = checked(totalBytes + length);
                }
                catch (OverflowException ex)
                {
                    throw new InvalidDataException(
                        "Abiotic Factor World declares an impossible total size.",
                        ex);
                }

                if (totalBytes > MaximumUncompressedBytes)
                {
                    throw new InvalidDataException(
                        $"Abiotic Factor World exceeds Steward's {MaximumUncompressedBytes}-byte package limit.");
                }

                var relative = Path.GetRelativePath(root, file)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (relative.Contains('\\') ||
                    relative.StartsWith("/", StringComparison.Ordinal) ||
                    relative.Split('/').Any(segment => segment is "" or "." or ".."))
                {
                    throw new InvalidDataException(
                        $"Abiotic Factor World produced a non-canonical relative path: {relative}");
                }

                result.Add(new WorldFile(Path.GetFullPath(file), relative));
            }
        }

        return result
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static PackageInspection InspectPackage(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumPackageEntries)
        {
            throw new InvalidDataException(
                $"Abiotic Factor state package must contain between 1 and {MaximumPackageEntries} file entries.");
        }

        var comparer = StringComparer.OrdinalIgnoreCase;
        var names = new HashSet<string>(comparer);
        var entries = new List<ZipArchiveEntry>(archive.Entries.Count);
        long totalBytes = 0;
        var foundWorldData = false;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name) ||
                string.IsNullOrWhiteSpace(entry.FullName) ||
                entry.FullName.Contains('\\') ||
                entry.FullName.StartsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Abiotic Factor state package contains an unsupported archive entry '{entry.FullName}'.");
            }

            var segments = entry.FullName.Split('/');
            if (segments.Any(segment => string.IsNullOrEmpty(segment) || segment is "." or "..") ||
                !names.Add(entry.FullName))
            {
                throw new InvalidDataException(
                    $"Abiotic Factor state package contains a non-canonical or colliding path '{entry.FullName}'.");
            }

            try
            {
                totalBytes = checked(totalBytes + entry.Length);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException(
                    "Abiotic Factor state package declares an impossible extraction size.",
                    ex);
            }

            if (totalBytes > MaximumUncompressedBytes)
            {
                throw new InvalidDataException(
                    $"Abiotic Factor state package exceeds Steward's {MaximumUncompressedBytes}-byte extraction limit.");
            }

            if (string.Equals(
                    Path.GetExtension(entry.Name),
                    ".sav",
                    StringComparison.OrdinalIgnoreCase) &&
                entry.Length > 0)
            {
                foundWorldData = true;
            }

            entries.Add(entry);
        }

        if (!foundWorldData)
        {
            throw new InvalidDataException(
                "Abiotic Factor state package does not contain non-empty World .sav data.");
        }

        return new PackageInspection(entries, totalBytes);
    }

    private static string GetSafeDestinationPath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(
            fullRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Abiotic Factor state package entry escapes its restore root: {relativePath}");
        }

        return candidate;
    }

    private static string GetPreparedWorldPath(string workingDirectory)
        => Path.GetFullPath(Path.Combine(
            workingDirectory,
            PreparedWorldRelativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static void RequireRegularPackageFile(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Abiotic Factor state package must be a .zip file: {path}");
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Abiotic Factor state package could not be inspected safely: {path}",
                ex);
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException(
                $"Abiotic Factor state package must be a regular non-linked file: {path}");
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
        catch (OverflowException ex)
        {
            throw new InvalidDataException(
                "Abiotic Factor state package declares an impossible file size.",
                ex);
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
                    $"Abiotic Factor state restore requires {required} bytes of free space, but only {drive.AvailableFreeSpace} bytes are available.");
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

    private sealed record WorldFile(string FullPath, string RelativePath);
    private sealed record PackageInspection(
        IReadOnlyList<ZipArchiveEntry> Entries,
        long TotalBytes);
}

internal static class AbioticFactorWorkspaceOwnership
{
    private const string AdapterId = "abiotic-factor";

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
                "Abiotic Factor cannot delete a SafeWorld-managed workspace root; Core owns that deletion by durable WorkspaceId.");
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
            $"Refusing to use unrecognized or linked Abiotic Factor SafeWorld workspace '{path}'.");
}
