using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.AbioticFactor;

internal static class AbioticFactorWorldState
{
    private const string PreparedWorldDirectoryName = "world";
    private const int MaximumArchiveEntries = 50_000;
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
        AbioticFactorWorkspaceOwnership.RequireOwned(world.WorkingDirectory);
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

    public static async Task RestorePreparedWorldAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        AbioticFactorWorkspaceOwnership.RequireOwned(world.WorkingDirectory);

        var packagePath = Path.GetFullPath(state.Path);
        RequireRegularPackageFile(packagePath);
        using var archive = ZipFile.OpenRead(packagePath);
        var inspection = InspectPackage(archive, cancellationToken);
        EnsureSufficientFreeSpace(world.WorkingDirectory, inspection.DeclaredBytes);

        var destinationPath = GetPreparedWorldPath(world.WorkingDirectory);
        var operationId = Guid.NewGuid().ToString("N");
        var stagingPath = destinationPath + ".sharedworlds-staging-" + operationId;
        var rollbackPath = destinationPath + ".sharedworlds-rollback-" + operationId;
        var movedExisting = false;

        try
        {
            Directory.CreateDirectory(stagingPath);
            await ExtractPackageAsync(
                inspection.Entries,
                stagingPath,
                cancellationToken);
            RequireWorldTree(stagingPath);

            if (Directory.Exists(destinationPath))
            {
                AbioticFactorWorkspaceOwnership.RequireOwnedTree(
                    world.WorkingDirectory);
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
        AbioticFactorWorkspaceOwnership.RequireOwned(world.WorkingDirectory);

        if (disposition == PreparedWorldDisposition.Discard &&
            Directory.Exists(world.WorkingDirectory))
        {
            AbioticFactorWorkspaceOwnership.RequireOwnedTree(world.WorkingDirectory);
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
        var files = EnumerateWorldFiles(root, cancellationToken);
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
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(
                        file.RelativePath,
                        CompressionLevel.Fastest);
                    await using var source = new FileStream(
                        file.FullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 128 * 1024,
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
        RequireWorldTree(root);
        var files = new List<WorldFile>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            RequireRegularDirectory(current, "Abiotic Factor World directory");

            foreach (var directory in Directory.EnumerateDirectories(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                RequireRegularDirectory(directory, "Abiotic Factor World directory");
                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireRegularFile(file, "Abiotic Factor World file");
                files.Add(new WorldFile(
                    Path.GetFullPath(file),
                    NormalizeEntryName(Path.GetRelativePath(root, file))));
                if (files.Count > MaximumArchiveEntries)
                {
                    throw new InvalidOperationException(
                        $"Abiotic Factor World contains more than Steward's {MaximumArchiveEntries} file-entry safety limit.");
                }
            }
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                "Abiotic Factor World directory does not contain any files.");
        }

        return files;
    }

    private static PackageInspection InspectPackage(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        var entries = new List<ZipArchiveEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredBytes = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                throw new InvalidDataException(
                    "Abiotic Factor state package must contain files only; explicit directory entries are not supported.");
            }

            var normalized = ValidateArchiveEntryName(entry.FullName);
            if (!names.Add(normalized))
            {
                throw new InvalidDataException(
                    $"Abiotic Factor state package contains a duplicate path '{normalized}'.");
            }

            entries.Add(entry);
            if (entries.Count > MaximumArchiveEntries)
            {
                throw new InvalidDataException(
                    $"Abiotic Factor state package exceeds Steward's {MaximumArchiveEntries} entry safety limit.");
            }

            try
            {
                declaredBytes = checked(declaredBytes + entry.Length);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException(
                    "Abiotic Factor state package declares an impossible extraction size.",
                    ex);
            }
        }

        if (entries.Count == 0)
        {
            throw new InvalidDataException(
                "Abiotic Factor state package does not contain any World files.");
        }

        if (!names.Contains(AbioticFactorWorldDiscovery.MetadataFileName))
        {
            throw new InvalidDataException(
                $"Abiotic Factor state package is missing root '{AbioticFactorWorldDiscovery.MetadataFileName}'.");
        }

        return new PackageInspection(entries, declaredBytes);
    }

    private static async Task ExtractPackageAsync(
        IReadOnlyList<ZipArchiveEntry> entries,
        string stagingPath,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(stagingPath) + Path.DirectorySeparatorChar;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = ValidateArchiveEntryName(entry.FullName)
                .Replace('/', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(stagingPath, relative));
            if (!destination.StartsWith(root, PathComparison))
            {
                throw new InvalidDataException(
                    $"Abiotic Factor state package entry escapes the owned workspace: '{entry.FullName}'.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = entry.Open();
            await using var target = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true);
            await source.CopyToAsync(target, cancellationToken);
            await target.FlushAsync(cancellationToken);
        }
    }

    private static string ValidateArchiveEntryName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith('/', StringComparison.Ordinal) ||
            value.StartsWith('\\') ||
            value.Contains('\\'))
        {
            throw new InvalidDataException(
                $"Abiotic Factor state package contains an invalid entry path '{value}'.");
        }

        var segments = value.Split('/', StringSplitOptions.None);
        if (segments.Any(segment =>
                string.IsNullOrEmpty(segment) ||
                segment is "." or ".."))
        {
            throw new InvalidDataException(
                $"Abiotic Factor state package contains an unsafe entry path '{value}'.");
        }

        return string.Join('/', segments);
    }

    private static string NormalizeEntryName(string relativePath)
    {
        var normalized = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
        {
            normalized = normalized.Replace(Path.AltDirectorySeparatorChar, '/');
        }

        return ValidateArchiveEntryName(normalized);
    }

    private static void RequireWorldTree(string path)
    {
        RequireRegularDirectory(path, "Abiotic Factor World directory");
        var metadata = Path.Combine(
            path,
            AbioticFactorWorldDiscovery.MetadataFileName);
        if (!AbioticFactorWorldDiscovery.IsRegularNonEmptyFile(metadata))
        {
            throw new InvalidOperationException(
                $"Abiotic Factor World is missing a regular non-empty '{AbioticFactorWorldDiscovery.MetadataFileName}' file: {path}");
        }
    }

    private static void RequireRegularDirectory(string path, string description)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"{description} could not be inspected safely: {path}",
                ex);
        }

        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"{description} must be a regular non-linked directory: {path}");
        }
    }

    private static void RequireRegularFile(string path, string description)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"{description} could not be inspected safely: {path}",
                ex);
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException(
                $"{description} must be a regular non-linked file: {path}");
        }
    }

    private static void RequireRegularPackageFile(string path)
    {
        if (!string.Equals(
                Path.GetExtension(path),
                ".zip",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Abiotic Factor state package must be a .zip file: {path}");
        }

        RequireRegularFile(path, "Abiotic Factor state package");
        if (new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException(
                $"Abiotic Factor state package is empty: {path}");
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
        catch (OverflowException ex)
        {
            throw new InvalidDataException(
                "Abiotic Factor state package declares an impossible extraction size.",
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

    private static string GetPreparedWorldPath(string workingDirectory)
        => Path.GetFullPath(Path.Combine(
            workingDirectory,
            PreparedWorldDirectoryName));

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
            "abiotic-factor",
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

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record WorldFile(string FullPath, string RelativePath);

    private sealed record PackageInspection(
        IReadOnlyList<ZipArchiveEntry> Entries,
        long DeclaredBytes);
}

internal static class AbioticFactorWorkspaceOwnership
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

    public static void RequireOwnedTree(string workingDirectory)
    {
        RequireOwned(workingDirectory);
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
            "abiotic-factor"));
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
            $"Refusing to use unrecognized or linked Abiotic Factor Steward workspace '{path}'.");
}
