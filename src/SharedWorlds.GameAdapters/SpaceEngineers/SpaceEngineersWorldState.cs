using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.SpaceEngineers;

internal static class SpaceEngineersWorldState
{
    private const string PreparedWorldRelativePath = "save/world";
    private const int MaximumPackageEntries = 100_000;
    private const long MaximumUncompressedBytes = 64L * 1024 * 1024 * 1024;
    private const long MinimumFreeSpaceReserveBytes = 256L * 1024 * 1024;

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
        SpaceEngineersWorkspaceOwnership.RequireOwned(world.WorkingDirectory);
        return CaptureDirectoryAsync(
            GetPreparedWorldPath(world.WorkingDirectory),
            world.DisplayName ?? "world",
            cancellationToken);
    }

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        SpaceEngineersEnvironment.RequireCompatible(installation, requiredEnvironment);
        return new PreparedWorld(
            installation,
            SpaceEngineersWorkspaceOwnership.Create(),
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
        SpaceEngineersWorkspaceOwnership.RequireOwned(world.WorkingDirectory);

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
                        $"Space Engineers package entry '{entry.FullName}' did not extract to the declared byte length.");
                }
            }

            if (!SpaceEngineersWorldDiscovery.TryInspectCurrentWorldTree(stagingPath))
            {
                throw new InvalidDataException(
                    "The restored Space Engineers package does not contain a usable current World tree.");
            }

            SpaceEngineersEnvironment.RequireVanillaWorld(stagingPath);

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (Directory.Exists(destinationPath))
            {
                SpaceEngineersWorkspaceOwnership.RequireOwnedTree(
                    world.WorkingDirectory,
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
                        "Space Engineers state restore failed and the previous prepared World could not be rolled back automatically.",
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
        SpaceEngineersWorkspaceOwnership.RequireOwned(world.WorkingDirectory);

        if (disposition == PreparedWorldDisposition.Discard &&
            Directory.Exists(world.WorkingDirectory))
        {
            SpaceEngineersWorkspaceOwnership.RequireOwnedTree(
                world.WorkingDirectory,
                world.WorkingDirectory);
            Directory.Delete(world.WorkingDirectory, recursive: true);
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
        SpaceEngineersEnvironment.RequireVanillaWorld(root);
        var files = EnumerateCurrentWorldFiles(root, cancellationToken);
        var packagePath = CreatePackagePath(worldName);

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
                    if (!SpaceEngineersWorldDiscovery.IsRegularFile(file.FullPath))
                    {
                        throw new InvalidOperationException(
                            $"Space Engineers World file became linked or unreadable before capture: {file.FullPath}");
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

    private static IReadOnlyList<WorldFile> EnumerateCurrentWorldFiles(
        string root,
        CancellationToken cancellationToken)
    {
        if (!SpaceEngineersWorldDiscovery.TryInspectCurrentWorldTree(root))
        {
            throw new InvalidOperationException(
                $"Space Engineers World must be a usable regular non-linked current World tree: {root}");
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
                if (PathsEqual(current, root) &&
                    string.Equals(
                        Path.GetFileName(directory),
                        SpaceEngineersWorldDiscovery.BackupDirectoryName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!SpaceEngineersWorldDiscovery.IsRegularDirectory(directory))
                {
                    throw new InvalidOperationException(
                        $"Space Engineers World contains a linked or unreadable directory: {directory}");
                }

                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (!SpaceEngineersWorldDiscovery.IsRegularFile(file))
                {
                    throw new InvalidOperationException(
                        $"Space Engineers World contains a linked or unreadable file: {file}");
                }

                if (result.Count >= MaximumPackageEntries)
                {
                    throw new InvalidDataException(
                        $"Space Engineers World exceeds Steward's {MaximumPackageEntries}-file package limit.");
                }

                var length = new FileInfo(file).Length;
                try
                {
                    totalBytes = checked(totalBytes + length);
                }
                catch (OverflowException ex)
                {
                    throw new InvalidDataException(
                        "Space Engineers World declares an impossible total size.",
                        ex);
                }

                if (totalBytes > MaximumUncompressedBytes)
                {
                    throw new InvalidDataException(
                        $"Space Engineers World exceeds Steward's {MaximumUncompressedBytes}-byte package limit.");
                }

                var relative = Path.GetRelativePath(root, file)
                    .Replace(Path.DirectorySeparatorChar, '/');
                ValidateRelativePath(relative);
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
                $"Space Engineers state package must contain between 1 and {MaximumPackageEntries} file entries.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ZipArchiveEntry>(archive.Entries.Count);
        long totalBytes = 0;
        var required = new HashSet<string>(
            [
                SpaceEngineersWorldDiscovery.SandboxFileName,
                SpaceEngineersWorldDiscovery.SandboxConfigFileName,
                SpaceEngineersWorldDiscovery.SectorFileName
            ],
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name) || string.IsNullOrWhiteSpace(entry.FullName))
            {
                throw new InvalidDataException(
                    $"Space Engineers state package contains an unsupported archive entry '{entry.FullName}'.");
            }

            ValidateRelativePath(entry.FullName);
            var firstSegment = entry.FullName.Split('/')[0];
            if (string.Equals(
                    firstSegment,
                    SpaceEngineersWorldDiscovery.BackupDirectoryName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Space Engineers state package contains native Backup recovery history, which is outside the current World revision.");
            }

            if (!names.Add(entry.FullName))
            {
                throw new InvalidDataException(
                    $"Space Engineers state package contains a colliding path '{entry.FullName}'.");
            }

            try
            {
                totalBytes = checked(totalBytes + entry.Length);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException(
                    "Space Engineers state package declares an impossible extraction size.",
                    ex);
            }

            if (totalBytes > MaximumUncompressedBytes)
            {
                throw new InvalidDataException(
                    $"Space Engineers state package exceeds Steward's {MaximumUncompressedBytes}-byte extraction limit.");
            }

            if (!entry.FullName.Contains('/'))
            {
                required.Remove(entry.FullName);
            }

            entries.Add(entry);
        }

        if (required.Count != 0)
        {
            throw new InvalidDataException(
                $"Space Engineers state package is missing required current World files: {string.Join(", ", required.OrderBy(name => name, StringComparer.Ordinal))}.");
        }

        return new PackageInspection(entries, totalBytes);
    }

    private static void ValidateRelativePath(string relative)
    {
        if (relative.Contains('\\') ||
            relative.StartsWith("/", StringComparison.Ordinal) ||
            relative.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw new InvalidDataException(
                $"Space Engineers state contains a non-canonical relative path: {relative}");
        }
    }

    private static string GetSafeDestinationPath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(
            fullRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(
                prefix,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Space Engineers state package entry escapes its restore root: {relativePath}");
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
                $"Space Engineers state package must be a .zip file: {path}");
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Space Engineers state package could not be inspected safely: {path}",
                ex);
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException(
                $"Space Engineers state package must be a regular non-linked file: {path}");
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
                "Space Engineers state package declares an impossible file size.",
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
                    $"Space Engineers state restore requires {required} bytes of free space, but only {drive.AvailableFreeSpace} bytes are available.");
            }
        }
        catch (DriveNotFoundException)
        {
        }
    }

    private static string CreatePackagePath(string worldName)
    {
        var root = GetPackageRoot();
        Directory.CreateDirectory(root);
        var safeName = SanitizeFileName(worldName);
        return Path.Combine(root, $"{safeName}-{Guid.NewGuid():N}.zip");
    }

    private static string GetPackageRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.GetTempPath();
        }

        return Path.Combine(
            localData,
            "SharedWorlds",
            "space-engineers",
            "packages");
    }

    private static string SanitizeFileName(string value)
    {
        var result = string.IsNullOrWhiteSpace(value) ? "world" : value;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            result = result.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(result) ? "world" : result;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

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

internal static class SpaceEngineersWorkspaceOwnership
{
    public static string Create()
    {
        var root = GetExpectedWorkRoot();
        Directory.CreateDirectory(root);
        var workspace = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return Path.GetFullPath(workspace);
    }

    public static void RequireOwned(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var workspace = Path.GetFullPath(workingDirectory);
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace));
        if (!Guid.TryParseExact(leaf, "N", out _) ||
            Directory.GetParent(workspace)?.FullName is not string ownerRoot ||
            !PathsEqual(ownerRoot, GetExpectedWorkRoot()))
        {
            throw Refuse(workingDirectory);
        }

        RejectReparsePoint(ownerRoot, workingDirectory);
        if (Directory.Exists(workspace))
        {
            RejectReparsePoint(workspace, workingDirectory);
        }
    }

    public static void RequireOwnedTree(string workingDirectory, string treeRoot)
    {
        RequireOwned(workingDirectory);
        var workspace = Path.GetFullPath(workingDirectory);
        var root = Path.GetFullPath(treeRoot);
        var prefix = Path.TrimEndingDirectorySeparator(workspace) + Path.DirectorySeparatorChar;
        if (!PathsEqual(root, workspace) &&
            !root.StartsWith(prefix, PathComparison))
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

    private static string GetExpectedWorkRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.GetTempPath();
        }

        return Path.GetFullPath(Path.Combine(
            localData,
            "Steward",
            "workspaces",
            "space-engineers"));
    }

    private static void RejectReparsePoint(string path, string originalPath)
    {
        if (Directory.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw Refuse(originalPath);
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static InvalidOperationException Refuse(string path)
        => new(
            $"Refusing to use unrecognized or linked Space Engineers Steward workspace '{path}'.");
}
