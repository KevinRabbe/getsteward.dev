using System.Globalization;
using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Storage;

namespace SharedWorlds.GameAdapters.CoreKeeper;

internal static class CoreKeeperWorldState
{
    private const string PreparedProfileDirectoryName = "profile";
    private const string WorldsDirectoryName = "worlds";
    private const string WorldInfosDirectoryName = "worldinfos";
    private const string WorldGenerationDirectoryName = "worldgenparams";
    private const string WorldSuffix = ".world.gzip";
    private const string WorldInfoSuffix = ".worldinfo";
    private const long MinimumFreeSpaceReserveBytes = 128L * 1024 * 1024;

    public static Task<CapturedState> CaptureDetectedWorldAsync(
        DetectedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        return CaptureBundleAsync(ResolveNativeBundle(world.SourcePath), cancellationToken);
    }

    public static Task<CapturedState> CapturePreparedWorldAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        CoreKeeperWorkspaceOwnership.RequireOwned(world.WorkingDirectory);
        return CaptureBundleAsync(
            FindSinglePreparedBundle(world.WorkingDirectory),
            cancellationToken);
    }

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        CoreKeeperEnvironment.RequireCompatible(installation, requiredEnvironment);
        return new PreparedWorld(
            installation,
            CoreKeeperWorkspaceOwnership.Create(),
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
        CoreKeeperWorkspaceOwnership.RequireOwned(world.WorkingDirectory);

        var packagePath = Path.GetFullPath(state.Path);
        RequireRegularFile(packagePath, "Core Keeper state package");

        using var archive = ZipFile.OpenRead(packagePath);
        var package = InspectPackage(archive, cancellationToken);
        EnsureSufficientFreeSpace(world.WorkingDirectory, package.DeclaredBytes);

        var profilePath = Path.Combine(
            world.WorkingDirectory,
            PreparedProfileDirectoryName);
        var operationId = Guid.NewGuid().ToString("N");
        var stagingRoot = Path.Combine(
            world.WorkingDirectory,
            $".sharedworlds-staging-{operationId}");
        var stagingProfile = Path.Combine(
            stagingRoot,
            PreparedProfileDirectoryName);
        var rollbackPath = Path.Combine(
            world.WorkingDirectory,
            $".sharedworlds-rollback-{operationId}");
        var movedExisting = false;

        try
        {
            await ExtractEntryAsync(
                package.WorldEntry,
                Path.Combine(
                    stagingProfile,
                    WorldsDirectoryName,
                    $"{package.Slot}{WorldSuffix}"),
                cancellationToken);
            await ExtractEntryAsync(
                package.WorldInfoEntry,
                Path.Combine(
                    stagingProfile,
                    WorldInfosDirectoryName,
                    $"{package.Slot}{WorldInfoSuffix}"),
                cancellationToken);
            await ExtractEntryAsync(
                package.GenerationEntry,
                Path.Combine(
                    stagingProfile,
                    WorldGenerationDirectoryName,
                    $"{package.Slot}.json"),
                cancellationToken);

            if (Directory.Exists(profilePath))
            {
                CoreKeeperWorkspaceOwnership.RequireOwnedTree(
                    world.WorkingDirectory,
                    profilePath);
                Directory.Move(profilePath, rollbackPath);
                movedExisting = true;
            }

            Directory.Move(stagingProfile, profilePath);
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
                !Directory.Exists(profilePath) &&
                Directory.Exists(rollbackPath))
            {
                try
                {
                    Directory.Move(rollbackPath, profilePath);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "Core Keeper state restore failed and the previous prepared World could not be rolled back automatically.",
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
        CoreKeeperWorkspaceOwnership.RequireOwned(world.WorkingDirectory);

        if (disposition == PreparedWorldDisposition.Discard)
        {
            CoreKeeperWorkspaceOwnership.DeleteOwned(world.WorkingDirectory);
        }

        return Task.CompletedTask;
    }

    private static async Task<CapturedState> CaptureBundleAsync(
        CoreKeeperNativeBundle bundle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var packagePath = DisposableStatePackageStorage.CreatePackagePath(
            "core-keeper",
            $"world-{bundle.Slot}",
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
                    $"{WorldsDirectoryName}/{bundle.Slot}{WorldSuffix}",
                    bundle.WorldPath,
                    cancellationToken);
                await WriteEntryAsync(
                    archive,
                    $"{WorldInfosDirectoryName}/{bundle.Slot}{WorldInfoSuffix}",
                    bundle.WorldInfoPath,
                    cancellationToken);
                await WriteEntryAsync(
                    archive,
                    $"{WorldGenerationDirectoryName}/{bundle.Slot}.json",
                    bundle.GenerationPath,
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
        RequireRegularFile(sourcePath, $"Core Keeper bundle file '{entryName}'");
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

    private static CoreKeeperNativeBundle ResolveNativeBundle(string worldPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldPath);
        var fullWorldPath = Path.GetFullPath(worldPath);
        RequireRegularFile(fullWorldPath, "Core Keeper World file");

        var fileName = Path.GetFileName(fullWorldPath);
        if (!fileName.EndsWith(WorldSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Core Keeper World file must end in '{WorldSuffix}': {fullWorldPath}");
        }

        var slot = fileName[..^WorldSuffix.Length];
        ValidateSlot(slot);

        var worldsRoot = Directory.GetParent(fullWorldPath)?.FullName
            ?? throw new InvalidOperationException(
                $"Could not determine Core Keeper worlds directory for '{fullWorldPath}'.");
        var profileRoot = Directory.GetParent(worldsRoot)?.FullName
            ?? throw new InvalidOperationException(
                $"Could not determine Core Keeper profile directory for '{fullWorldPath}'.");

        RequireRegularDirectory(profileRoot, "Core Keeper Steam save profile");
        RequireRegularDirectory(worldsRoot, "Core Keeper worlds directory");
        if (!string.Equals(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(worldsRoot)),
                WorldsDirectoryName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Core Keeper World file is not under a '{WorldsDirectoryName}' directory: {fullWorldPath}");
        }

        var worldInfoPath = Path.Combine(
            profileRoot,
            WorldInfosDirectoryName,
            $"{slot}{WorldInfoSuffix}");
        var generationPath = Path.Combine(
            profileRoot,
            WorldGenerationDirectoryName,
            $"{slot}.json");
        RequireRegularDirectory(
            Path.GetDirectoryName(worldInfoPath)!,
            "Core Keeper worldinfos directory");
        RequireRegularDirectory(
            Path.GetDirectoryName(generationPath)!,
            "Core Keeper worldgenparams directory");
        RequireRegularFile(worldInfoPath, "Core Keeper World info file");
        RequireRegularFile(
            generationPath,
            "Core Keeper World generation parameters");

        return new CoreKeeperNativeBundle(
            slot,
            fullWorldPath,
            Path.GetFullPath(worldInfoPath),
            Path.GetFullPath(generationPath));
    }

    private static CoreKeeperNativeBundle FindSinglePreparedBundle(
        string workingDirectory)
    {
        var profileRoot = Path.Combine(
            workingDirectory,
            PreparedProfileDirectoryName);
        var worldsRoot = Path.Combine(profileRoot, WorldsDirectoryName);
        RequireRegularDirectory(profileRoot, "prepared Core Keeper profile");
        RequireRegularDirectory(
            worldsRoot,
            "prepared Core Keeper worlds directory");

        var candidates = Directory
            .EnumerateFiles(
                worldsRoot,
                $"*{WorldSuffix}",
                SearchOption.TopDirectoryOnly)
            .Where(CoreKeeperWorldDiscovery.IsRegularFile)
            .Take(2)
            .ToArray();
        if (candidates.Length != 1)
        {
            throw new InvalidOperationException(
                $"Prepared Core Keeper workspace must contain exactly one World; found {candidates.Length}.");
        }

        return ResolveNativeBundle(candidates[0]);
    }

    private static CoreKeeperPackageInspection InspectPackage(
        ZipArchive archive,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .ToArray();
        if (files.Length != 3)
        {
            throw new InvalidDataException(
                $"Core Keeper state package must contain exactly three current World files; found {files.Length}.");
        }

        string? slot = null;
        ZipArchiveEntry? worldEntry = null;
        ZipArchiveEntry? worldInfoEntry = null;
        ZipArchiveEntry? generationEntry = null;
        long declaredBytes = 0;

        try
        {
            foreach (var entry in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.FullName.Contains('\\'))
                {
                    throw new InvalidDataException(
                        $"Core Keeper state package contains a non-canonical path: '{entry.FullName}'.");
                }

                var segments = entry.FullName.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length != 2)
                {
                    throw new InvalidDataException(
                        $"Core Keeper state package contains an unexpected path: '{entry.FullName}'.");
                }

                string entrySlot;
                if (string.Equals(
                        segments[0],
                        WorldsDirectoryName,
                        StringComparison.Ordinal) &&
                    segments[1].EndsWith(
                        WorldSuffix,
                        StringComparison.Ordinal))
                {
                    entrySlot = segments[1][..^WorldSuffix.Length];
                    if (worldEntry is not null)
                    {
                        throw new InvalidDataException(
                            "Core Keeper state package contains duplicate World data files.");
                    }

                    worldEntry = entry;
                }
                else if (string.Equals(
                             segments[0],
                             WorldInfosDirectoryName,
                             StringComparison.Ordinal) &&
                         segments[1].EndsWith(
                             WorldInfoSuffix,
                             StringComparison.Ordinal))
                {
                    entrySlot = segments[1][..^WorldInfoSuffix.Length];
                    if (worldInfoEntry is not null)
                    {
                        throw new InvalidDataException(
                            "Core Keeper state package contains duplicate World info files.");
                    }

                    worldInfoEntry = entry;
                }
                else if (string.Equals(
                             segments[0],
                             WorldGenerationDirectoryName,
                             StringComparison.Ordinal) &&
                         segments[1].EndsWith(
                             ".json",
                             StringComparison.Ordinal))
                {
                    entrySlot = segments[1][..^".json".Length];
                    if (generationEntry is not null)
                    {
                        throw new InvalidDataException(
                            "Core Keeper state package contains duplicate World generation files.");
                    }

                    generationEntry = entry;
                }
                else
                {
                    throw new InvalidDataException(
                        $"Core Keeper state package contains unsupported file '{entry.FullName}'.");
                }

                ValidateSlot(entrySlot);
                slot ??= entrySlot;
                if (!string.Equals(slot, entrySlot, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Core Keeper state package contains files from more than one World slot.");
                }

                declaredBytes = checked(declaredBytes + entry.Length);
            }
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "Core Keeper state package declares an impossible extraction size.",
                exception);
        }

        if (slot is null ||
            worldEntry is null ||
            worldInfoEntry is null ||
            generationEntry is null)
        {
            throw new InvalidDataException(
                "Core Keeper state package is missing World data, World info, or World generation parameters.");
        }

        return new CoreKeeperPackageInspection(
            slot,
            worldEntry,
            worldInfoEntry,
            generationEntry,
            declaredBytes);
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
                "Core Keeper state package declares an impossible extraction size.",
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
                    $"Core Keeper state package requires {required} bytes of free space for safe extraction, but only {drive.AvailableFreeSpace} bytes are available.");
            }
        }
        catch (DriveNotFoundException)
        {
        }
    }

    private static void ValidateSlot(string slot)
    {
        if (!int.TryParse(
                slot,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var slotNumber) ||
            slotNumber < 0 ||
            !string.Equals(
                slotNumber.ToString(CultureInfo.InvariantCulture),
                slot,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Core Keeper World slot '{slot}' is not a canonical non-negative integer.");
        }
    }

    private static void RequireRegularDirectory(
        string path,
        string description)
    {
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

        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"{description} must be a regular non-linked directory: {path}");
        }
    }

    private static void RequireRegularFile(
        string path,
        string description)
    {
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

    private sealed record CoreKeeperNativeBundle(
        string Slot,
        string WorldPath,
        string WorldInfoPath,
        string GenerationPath);

    private sealed record CoreKeeperPackageInspection(
        string Slot,
        ZipArchiveEntry WorldEntry,
        ZipArchiveEntry WorldInfoEntry,
        ZipArchiveEntry GenerationEntry,
        long DeclaredBytes);
}

internal static class CoreKeeperWorkspaceOwnership
{
    private const string AdapterId = "core-keeper";

    public static string Create()
        => DisposablePreparedWorkspaceStorage.Create(AdapterId);

    public static void RequireOwned(string workingDirectory)
        => _ = DisposablePreparedWorkspaceStorage.RequireOwned(
            AdapterId,
            workingDirectory);

    public static void RequireOwnedTree(
        string workingDirectory,
        string treeRoot)
        => DisposablePreparedWorkspaceStorage.RequireOwnedTree(
            AdapterId,
            workingDirectory,
            treeRoot);

    public static void DeleteOwned(string workingDirectory)
        => DisposablePreparedWorkspaceStorage.DeleteOwned(
            AdapterId,
            workingDirectory);
}
