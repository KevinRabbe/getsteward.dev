using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.VRising;

internal static partial class VRisingWorldState
{
    private const string PreparedSessionDirectoryName = "00000000-0000-0000-0000-000000000000";
    private const long MinimumFreeSpaceReserveBytes = 128L * 1024 * 1024;

    public static Task<CapturedState> CaptureDetectedWorldAsync(
        DetectedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        return CaptureSessionAsync(
            world.SourcePath,
            world.DisplayName,
            cancellationToken);
    }

    public static Task<CapturedState> CapturePreparedWorldAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        VRisingWorkspaceOwnership.RequireOwned(world.WorkingDirectory);
        return CaptureSessionAsync(
            GetPreparedSessionPath(world.WorkingDirectory),
            world.DisplayName ?? "world",
            cancellationToken);
    }

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        VRisingEnvironment.RequireCompatible(installation, requiredEnvironment);
        return new PreparedWorld(
            installation,
            VRisingWorkspaceOwnership.Create(),
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
        VRisingWorkspaceOwnership.RequireOwned(world.WorkingDirectory);

        var packagePath = Path.GetFullPath(state.Path);
        RequireRegularPackageFile(packagePath);
        using var archive = ZipFile.OpenRead(packagePath);
        var package = InspectPackage(archive, cancellationToken);
        EnsureSufficientFreeSpace(world.WorkingDirectory, package.DeclaredBytes);

        var sessionPath = GetPreparedSessionPath(world.WorkingDirectory);
        var operationId = Guid.NewGuid().ToString("N");
        var stagingRoot = Path.Combine(
            world.WorkingDirectory,
            $".sharedworlds-staging-{operationId}");
        var stagingSession = Path.Combine(stagingRoot, PreparedSessionDirectoryName);
        var rollbackPath = Path.Combine(
            world.WorkingDirectory,
            $".sharedworlds-rollback-{operationId}");
        var movedExisting = false;

        try
        {
            Directory.CreateDirectory(stagingSession);
            foreach (var entry in package.Entries)
            {
                await ExtractEntryAsync(
                    entry,
                    Path.Combine(stagingSession, entry.Name),
                    cancellationToken);
            }

            _ = VRisingWorldDiscovery.ResolveCurrentBundle(stagingSession);

            if (Directory.Exists(sessionPath))
            {
                VRisingWorkspaceOwnership.RequireOwnedTree(
                    world.WorkingDirectory,
                    sessionPath);
                Directory.Move(sessionPath, rollbackPath);
                movedExisting = true;
            }

            Directory.Move(stagingSession, sessionPath);
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
                !Directory.Exists(sessionPath) &&
                Directory.Exists(rollbackPath))
            {
                try
                {
                    Directory.Move(rollbackPath, sessionPath);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "V Rising state restore failed and the previous prepared World could not be rolled back automatically.",
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
        VRisingWorkspaceOwnership.RequireOwned(world.WorkingDirectory);

        if (disposition == PreparedWorldDisposition.Discard &&
            Directory.Exists(world.WorkingDirectory))
        {
            VRisingWorkspaceOwnership.RequireOwnedTree(
                world.WorkingDirectory,
                world.WorkingDirectory);
            Directory.Delete(world.WorkingDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static async Task<CapturedState> CaptureSessionAsync(
        string sessionPath,
        string worldName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var before = VRisingWorldDiscovery.ResolveCurrentBundle(sessionPath);
        var beforeFingerprint = Snapshot(before);
        var packagePath = CreatePackagePath(worldName);

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
                    Path.GetFileName(before.AutoSavePath),
                    before.AutoSavePath,
                    cancellationToken);
                await WriteEntryAsync(
                    archive,
                    VRisingWorldDiscovery.ServerGameSettingsFileName,
                    before.ServerGameSettingsPath,
                    cancellationToken);
                await WriteEntryAsync(
                    archive,
                    VRisingWorldDiscovery.SessionIdFileName,
                    before.SessionIdPath,
                    cancellationToken);
                await WriteEntryAsync(
                    archive,
                    VRisingWorldDiscovery.StartDateFileName,
                    before.StartDatePath,
                    cancellationToken);
            }

            await packageStream.FlushAsync(cancellationToken);

            var after = VRisingWorldDiscovery.ResolveCurrentBundle(sessionPath);
            var afterFingerprint = Snapshot(after);
            if (before.AutoSaveGeneration != after.AutoSaveGeneration ||
                !string.Equals(
                    Path.GetFileName(before.AutoSavePath),
                    Path.GetFileName(after.AutoSavePath),
                    StringComparison.OrdinalIgnoreCase) ||
                beforeFingerprint != afterFingerprint)
            {
                throw new InvalidOperationException(
                    "V Rising session changed while Steward was capturing it. The temporary package was discarded; retry after the World is idle.");
            }

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
        RequireRegularNonEmptyFile(sourcePath, $"V Rising state file '{entryName}'");
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        var expectedLength = source.Length;
        await using var destination = entry.Open();
        await source.CopyToAsync(destination, cancellationToken);
        if (source.Length != expectedLength)
        {
            throw new InvalidOperationException(
                $"V Rising state file changed length during capture: {sourcePath}");
        }
    }

    private static VRisingPackageInspection InspectPackage(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .ToArray();
        if (files.Length != 4 || archive.Entries.Count != 4)
        {
            throw new InvalidDataException(
                $"V Rising state package must contain exactly four current World files; found {archive.Entries.Count} entries.");
        }

        ZipArchiveEntry? autoSave = null;
        ZipArchiveEntry? gameSettings = null;
        ZipArchiveEntry? sessionId = null;
        ZipArchiveEntry? startDate = null;
        long declaredBytes = 0;

        try
        {
            foreach (var entry in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(entry.FullName, entry.Name, StringComparison.Ordinal) ||
                    entry.Name.Contains('/') ||
                    entry.Name.Contains('\\') ||
                    entry.Length <= 0)
                {
                    throw new InvalidDataException(
                        $"V Rising state package contains a non-canonical or empty entry: '{entry.FullName}'.");
                }

                if (AutoSaveRegex().IsMatch(entry.Name))
                {
                    if (autoSave is not null)
                    {
                        throw new InvalidDataException(
                            "V Rising state package contains more than one autosave generation.");
                    }

                    autoSave = entry;
                }
                else if (string.Equals(
                             entry.Name,
                             VRisingWorldDiscovery.ServerGameSettingsFileName,
                             StringComparison.Ordinal))
                {
                    gameSettings = RequireUnique(gameSettings, entry);
                }
                else if (string.Equals(
                             entry.Name,
                             VRisingWorldDiscovery.SessionIdFileName,
                             StringComparison.Ordinal))
                {
                    sessionId = RequireUnique(sessionId, entry);
                }
                else if (string.Equals(
                             entry.Name,
                             VRisingWorldDiscovery.StartDateFileName,
                             StringComparison.Ordinal))
                {
                    startDate = RequireUnique(startDate, entry);
                }
                else
                {
                    throw new InvalidDataException(
                        $"V Rising state package contains unsupported file '{entry.Name}'.");
                }

                declaredBytes = checked(declaredBytes + entry.Length);
            }
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "V Rising state package declares an impossible extraction size.",
                exception);
        }

        if (autoSave is null || gameSettings is null || sessionId is null || startDate is null)
        {
            throw new InvalidDataException(
                "V Rising state package is missing the current autosave or required World metadata.");
        }

        return new VRisingPackageInspection(
            [autoSave, gameSettings, sessionId, startDate],
            declaredBytes);
    }

    private static ZipArchiveEntry RequireUnique(
        ZipArchiveEntry? existing,
        ZipArchiveEntry candidate)
    {
        if (existing is not null)
        {
            throw new InvalidDataException(
                $"V Rising state package contains duplicate '{candidate.Name}' entries.");
        }

        return candidate;
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
        if (destination.Length != entry.Length)
        {
            throw new InvalidDataException(
                $"V Rising package entry '{entry.Name}' did not extract to its declared byte length.");
        }
    }

    private static VRisingBundleFingerprint Snapshot(VRisingNativeBundle bundle)
        => new(
            SnapshotFile(bundle.AutoSavePath),
            SnapshotFile(bundle.ServerGameSettingsPath),
            SnapshotFile(bundle.SessionIdPath),
            SnapshotFile(bundle.StartDatePath));

    private static VRisingFileFingerprint SnapshotFile(string path)
    {
        RequireRegularNonEmptyFile(path, "V Rising state file");
        var info = new FileInfo(path);
        return new VRisingFileFingerprint(
            Path.GetFileName(path),
            info.Length,
            info.LastWriteTimeUtc.Ticks);
    }

    private static void RequireRegularNonEmptyFile(string path, string description)
    {
        if (!VRisingWorldDiscovery.IsRegularNonEmptyFile(path))
        {
            throw new InvalidOperationException(
                $"{description} must be a regular non-linked non-empty file: {path}");
        }
    }

    private static void RequireRegularPackageFile(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"V Rising state package must be a .zip file: {path}");
        }

        RequireRegularNonEmptyFile(path, "V Rising state package");
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
                "V Rising state package declares an impossible extraction size.",
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
                    $"V Rising state restore requires {required} bytes of free space, but only {drive.AvailableFreeSpace} bytes are available.");
            }
        }
        catch (DriveNotFoundException)
        {
        }
    }

    private static string GetPreparedSessionPath(string workingDirectory)
        => Path.GetFullPath(Path.Combine(
            workingDirectory,
            PreparedSessionDirectoryName));

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
            "v-rising",
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

    [GeneratedRegex("^AutoSave_([0-9]+)\\.save(?:\\.gz)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AutoSaveRegex();
}

internal static class VRisingWorkspaceOwnership
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

    public static void RequireOwnedTree(
        string workingDirectory,
        string treePath)
    {
        RequireOwned(workingDirectory);
        if (!Directory.Exists(treePath))
        {
            return;
        }

        var workspace = Path.GetFullPath(workingDirectory);
        var root = Path.GetFullPath(treePath);
        var relative = Path.GetRelativePath(workspace, root);
        if (relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            throw Refuse(treePath);
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
            "v-rising"));
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

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static InvalidOperationException Refuse(string path)
        => new(
            $"Refusing to use unrecognized or linked V Rising Steward workspace '{path}'.");
}

internal sealed record VRisingPackageInspection(
    IReadOnlyList<ZipArchiveEntry> Entries,
    long DeclaredBytes);

internal sealed record VRisingFileFingerprint(
    string FileName,
    long Length,
    long LastWriteTimeUtcTicks);

internal sealed record VRisingBundleFingerprint(
    VRisingFileFingerprint AutoSave,
    VRisingFileFingerprint ServerGameSettings,
    VRisingFileFingerprint SessionId,
    VRisingFileFingerprint StartDate);
