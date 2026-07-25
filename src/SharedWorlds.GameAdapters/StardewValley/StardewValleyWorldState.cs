using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.StardewValley;

internal static class StardewValleyWorldState
{
    private const string SaveRootDirectoryName = "Saves";
    private const string SaveGameInfoFileName = "SaveGameInfo";
    private const long MinimumFreeSpaceReserveBytes = 128L * 1024 * 1024;

    public static Task<CapturedState> CaptureDetectedWorldAsync(
        DetectedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        return CaptureSaveDirectoryAsync(world.SourcePath, cancellationToken);
    }

    public static Task<CapturedState> CapturePreparedWorldAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        StardewValleyWorkspaceOwnership.RequireOwned(world.WorkingDirectory);
        return CaptureSaveDirectoryAsync(FindSinglePreparedSaveDirectory(world.WorkingDirectory), cancellationToken);
    }

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        StardewValleyEnvironment.RequireCompatible(installation, requiredEnvironment);
        return new PreparedWorld(
            installation,
            StardewValleyWorkspaceOwnership.Create(),
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
        StardewValleyWorkspaceOwnership.RequireOwned(world.WorkingDirectory);

        var packagePath = Path.GetFullPath(state.Path);
        RequireRegularFile(packagePath, "Stardew Valley state package");

        using var archive = ZipFile.OpenRead(packagePath);
        var bundle = InspectBundle(archive, cancellationToken);
        EnsureSufficientFreeSpace(world.WorkingDirectory, bundle.DeclaredBytes);

        var savesRoot = Path.Combine(world.WorkingDirectory, SaveRootDirectoryName);
        Directory.CreateDirectory(savesRoot);
        var destination = Path.Combine(savesRoot, bundle.SaveName);
        var operationId = Guid.NewGuid().ToString("N");
        var stagingParent = Path.Combine(world.WorkingDirectory, $".sharedworlds-staging-{operationId}");
        var staging = Path.Combine(stagingParent, bundle.SaveName);
        var rollback = Path.Combine(world.WorkingDirectory, $".sharedworlds-rollback-{operationId}");
        var movedExisting = false;

        try
        {
            Directory.CreateDirectory(staging);
            await ExtractEntryAsync(bundle.MainEntry, Path.Combine(staging, bundle.SaveName), cancellationToken);
            await ExtractEntryAsync(bundle.InfoEntry, Path.Combine(staging, SaveGameInfoFileName), cancellationToken);

            if (Directory.Exists(destination))
            {
                StardewValleyWorkspaceOwnership.RequireOwnedTree(world.WorkingDirectory, destination);
                Directory.Move(destination, rollback);
                movedExisting = true;
            }

            Directory.Move(staging, destination);
            TryDeleteDirectory(stagingParent);
            if (movedExisting)
            {
                TryDeleteDirectory(rollback);
            }
        }
        catch (Exception restoreException)
        {
            TryDeleteDirectory(stagingParent);
            if (movedExisting && !Directory.Exists(destination) && Directory.Exists(rollback))
            {
                try
                {
                    Directory.Move(rollback, destination);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Stardew Valley state restore failed and the previous prepared save could not be rolled back automatically.",
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
        StardewValleyWorkspaceOwnership.RequireOwned(world.WorkingDirectory);

        if (disposition == PreparedWorldDisposition.Discard && Directory.Exists(world.WorkingDirectory))
        {
            StardewValleyWorkspaceOwnership.RequireOwnedTree(world.WorkingDirectory, world.WorkingDirectory);
            Directory.Delete(world.WorkingDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static async Task<CapturedState> CaptureSaveDirectoryAsync(
        string sourceDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(sourceDirectory);
        RequireRegularDirectory(source, "Stardew Valley save directory");
        var saveName = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        ValidateSaveName(saveName);

        var mainPath = Path.Combine(source, saveName);
        var infoPath = Path.Combine(source, SaveGameInfoFileName);
        RequireRegularFile(mainPath, "Stardew Valley main save file");
        RequireRegularFile(infoPath, "Stardew Valley SaveGameInfo file");

        var packagePath = CreatePackagePath(saveName);
        try
        {
            await using var main = OpenStableSource(mainPath);
            await using var info = OpenStableSource(infoPath);
            await using var package = new FileStream(
                packagePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true);
            using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
            {
                await WriteEntryAsync(archive, $"{saveName}/{saveName}", main, cancellationToken);
                await WriteEntryAsync(archive, $"{saveName}/{SaveGameInfoFileName}", info, cancellationToken);
            }

            await package.FlushAsync(cancellationToken);
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

    private static FileStream OpenStableSource(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string entryName,
        Stream source,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var destination = entry.Open();
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static BundleInspection InspectBundle(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToArray();
        if (files.Length != 2)
        {
            throw new InvalidDataException(
                $"Stardew Valley state package must contain exactly two current save files; found {files.Length}.");
        }

        string? saveName = null;
        ZipArchiveEntry? main = null;
        ZipArchiveEntry? info = null;
        long declaredBytes = 0;
        try
        {
            foreach (var entry in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.FullName.Contains('\\', StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Stardew Valley state package contains a non-canonical path: '{entry.FullName}'.");
                }

                var segments = entry.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length != 2)
                {
                    throw new InvalidDataException(
                        $"Stardew Valley state package contains an unexpected path: '{entry.FullName}'.");
                }

                ValidateSaveName(segments[0]);
                saveName ??= segments[0];
                if (!string.Equals(saveName, segments[0], StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Stardew Valley state package contains more than one save root.");
                }

                if (string.Equals(segments[1], saveName, StringComparison.Ordinal))
                {
                    if (main is not null)
                    {
                        throw new InvalidDataException("Stardew Valley state package contains duplicate main save files.");
                    }

                    main = entry;
                }
                else if (string.Equals(segments[1], SaveGameInfoFileName, StringComparison.Ordinal))
                {
                    if (info is not null)
                    {
                        throw new InvalidDataException("Stardew Valley state package contains duplicate SaveGameInfo files.");
                    }

                    info = entry;
                }
                else
                {
                    throw new InvalidDataException(
                        $"Stardew Valley state package contains unsupported file '{entry.FullName}'.");
                }

                declaredBytes = checked(declaredBytes + entry.Length);
            }
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "Stardew Valley state package declares an impossible extraction size.",
                exception);
        }

        if (saveName is null || main is null || info is null)
        {
            throw new InvalidDataException(
                "Stardew Valley state package is missing its current main save file or SaveGameInfo.");
        }

        return new BundleInspection(saveName, main, info, declaredBytes);
    }

    private static async Task ExtractEntryAsync(
        ZipArchiveEntry entry,
        string destinationPath,
        CancellationToken cancellationToken)
    {
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
    }

    private static string FindSinglePreparedSaveDirectory(string workingDirectory)
    {
        var savesRoot = Path.Combine(workingDirectory, SaveRootDirectoryName);
        RequireRegularDirectory(savesRoot, "prepared Stardew Valley Saves directory");
        var candidates = Directory
            .EnumerateDirectories(savesRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => StardewValleyWorldDiscovery.IsValidCurrentSaveDirectory(path))
            .Take(2)
            .ToArray();
        if (candidates.Length != 1)
        {
            throw new InvalidOperationException(
                $"Prepared Stardew Valley workspace must contain exactly one valid save directory; found {candidates.Length}.");
        }

        return candidates[0];
    }

    private static void EnsureSufficientFreeSpace(string workingDirectory, long declaredBytes)
    {
        long required;
        try
        {
            required = checked(declaredBytes + MinimumFreeSpaceReserveBytes);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("Stardew Valley state package declares an impossible extraction size.", exception);
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
                    $"Stardew Valley state package requires {required} bytes of free space for safe extraction, but only {drive.AvailableFreeSpace} bytes are available.");
            }
        }
        catch (DriveNotFoundException)
        {
        }
    }

    private static void ValidateSaveName(string saveName)
    {
        if (string.IsNullOrWhiteSpace(saveName) ||
            saveName is "." or ".." ||
            saveName.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|', '\0']) >= 0)
        {
            throw new InvalidDataException($"Stardew Valley state package contains unsafe save name '{saveName}'.");
        }
    }

    private static void RequireRegularDirectory(string path, string description)
    {
        var attributes = GetAttributes(path, description);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"{description} must be a regular non-linked directory: {path}");
        }
    }

    private static void RequireRegularFile(string path, string description)
    {
        var attributes = GetAttributes(path, description);
        if ((attributes & FileAttributes.Directory) != 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"{description} must be a regular non-linked file: {path}");
        }
    }

    private static FileAttributes GetAttributes(string path, string description)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{description} could not be inspected safely: {path}", exception);
        }
    }

    private static string CreatePackagePath(string saveName)
    {
        var root = GetPackageRoot();
        Directory.CreateDirectory(root);
        return Path.Combine(root, $"{saveName}-{Guid.NewGuid():N}.zip");
    }

    private static string GetPackageRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.GetTempPath();
        }

        return Path.Combine(localData, "SharedWorlds", "stardew-valley", "packages");
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

    private sealed record BundleInspection(
        string SaveName,
        ZipArchiveEntry MainEntry,
        ZipArchiveEntry InfoEntry,
        long DeclaredBytes);
}

internal static class StardewValleyWorkspaceOwnership
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
        var prefix = workspace + Path.DirectorySeparatorChar;
        if (!PathsEqual(root, workspace) && !root.StartsWith(prefix, PathComparison))
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
            foreach (var directory in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
            {
                RejectReparsePoint(directory, treeRoot);
                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly))
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

        return Path.GetFullPath(Path.Combine(localData, "Steward", "workspaces", "stardew-valley"));
    }

    private static void RejectReparsePoint(string path, string originalPath)
    {
        if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw Refuse(originalPath);
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static InvalidOperationException Refuse(string path)
        => new($"Refusing to use unrecognized or linked Stardew Valley Steward workspace '{path}'.");
}
