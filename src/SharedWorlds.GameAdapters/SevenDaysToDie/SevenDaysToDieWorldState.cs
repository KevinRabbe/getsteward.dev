using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal static class SevenDaysToDieWorldState
{
    public static Task<CapturedState> CaptureDetectedWorldAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(world);
        var userDataRoot = GetRequiredUserDataRoot(installation);
        var identity = GetDetectedWorldIdentity(userDataRoot, world.SourcePath);
        return CaptureWorldBundleAsync(
            userDataRoot,
            identity.WorldName,
            identity.GameName,
            cancellationToken);
    }

    public static Task<CapturedState> CapturePreparedWorldAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        var userDataRoot = Path.GetFullPath(world.WorkingDirectory);
        var identity = ValidateSingleWorldBundle(userDataRoot);
        return CaptureWorldBundleAsync(
            userDataRoot,
            identity.WorldName,
            identity.GameName,
            cancellationToken);
    }

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(requiredEnvironment);
        var verification = SevenDaysToDieEnvironment.Verify(installation, requiredEnvironment);
        if (!verification.IsReady)
        {
            throw new EnvironmentReproductionException(
                "7-days-to-die",
                string.Join("; ", verification.Issues.Select(issue => issue.Message)));
        }

        var operationRoot = Path.Combine(
            GetLocalWorkRoot(),
            Guid.NewGuid().ToString("N"));
        var userDataRoot = Path.Combine(operationRoot, "user-data");
        Directory.CreateDirectory(operationRoot);
        return new PreparedWorld(
            installation,
            userDataRoot,
            requiredEnvironment);
    }

    public static async Task RestorePreparedWorldAsync(
        PreparedWorld world,
        StatePackage state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        var packagePath = Path.GetFullPath(state.Path);
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException(
                "The 7 Days to Die state package does not exist.",
                packagePath);
        }

        var destinationRoot = Path.GetFullPath(world.WorkingDirectory);
        var parentRoot = Path.GetDirectoryName(destinationRoot)
            ?? throw new InvalidOperationException(
                $"Could not determine the parent directory for 7 Days to Die workspace '{destinationRoot}'.");
        Directory.CreateDirectory(parentRoot);

        var operationId = Guid.NewGuid().ToString("N");
        var stagingRoot = destinationRoot + ".sharedworlds-staging-" + operationId;
        var rollbackRoot = destinationRoot + ".sharedworlds-rollback-" + operationId;
        var movedExisting = false;

        try
        {
            await ExtractPackageAsync(packagePath, stagingRoot, cancellationToken);
            ValidateSingleWorldBundle(stagingRoot);

            if (Directory.Exists(destinationRoot))
            {
                Directory.Move(destinationRoot, rollbackRoot);
                movedExisting = true;
            }

            Directory.Move(stagingRoot, destinationRoot);
            if (movedExisting)
            {
                TryDeleteDirectory(rollbackRoot);
            }
        }
        catch (Exception restoreException)
        {
            TryDeleteDirectory(stagingRoot);
            if (movedExisting &&
                !Directory.Exists(destinationRoot) &&
                Directory.Exists(rollbackRoot))
            {
                try
                {
                    Directory.Move(rollbackRoot, destinationRoot);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "7 Days to Die state restore failed and the previous prepared World could not be rolled back automatically.",
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
        if (disposition == PreparedWorldDisposition.PreserveForRecovery)
        {
            return Task.CompletedTask;
        }

        var userDataRoot = Path.GetFullPath(world.WorkingDirectory);
        var operationRoot = Directory.GetParent(userDataRoot)?.FullName;
        if (operationRoot is not null)
        {
            TryDeleteDirectory(operationRoot);
        }

        return Task.CompletedTask;
    }

    private static async Task<CapturedState> CaptureWorldBundleAsync(
        string userDataRoot,
        string worldName,
        string gameName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullUserDataRoot = Path.GetFullPath(userDataRoot);
        ValidateWorldBundle(
            fullUserDataRoot,
            worldName,
            gameName,
            requireExclusiveBundle: false);

        var packagePath = Path.Combine(
            Path.GetTempPath(),
            $"sharedworlds-7dtd-{Guid.NewGuid():N}.zip");
        try
        {
            await using var packageStream = new FileStream(
                packagePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true);
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: true);

            var saveRoot = Path.Combine(fullUserDataRoot, "Saves", worldName, gameName);
            await AddDirectoryAsync(
                archive,
                saveRoot,
                Path.Combine("Saves", worldName, gameName),
                cancellationToken);

            var generatedWorldRoot = Path.Combine(fullUserDataRoot, "GeneratedWorlds", worldName);
            if (Directory.Exists(generatedWorldRoot))
            {
                await AddDirectoryAsync(
                    archive,
                    generatedWorldRoot,
                    Path.Combine("GeneratedWorlds", worldName),
                    cancellationToken);
            }

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

    private static async Task AddDirectoryAsync(
        ZipArchive archive,
        string sourceRoot,
        string entryRoot,
        CancellationToken cancellationToken)
    {
        foreach (var filePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceRoot, filePath);
            var entryPath = Path.Combine(entryRoot, relativePath);
            var entryName = entryPath.Replace(Path.DirectorySeparatorChar, '/');
            if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
            {
                entryName = entryName.Replace(Path.AltDirectorySeparatorChar, '/');
            }

            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            await using var sourceStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                useAsync: true);
            await using var destinationStream = entry.Open();
            await sourceStream.CopyToAsync(destinationStream, cancellationToken);
        }
    }

    private static async Task ExtractPackageAsync(
        string packagePath,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingRoot);
        var fullStagingRoot = Path.GetFullPath(stagingRoot);
        var stagingPrefix = fullStagingRoot + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var extractedFiles = new HashSet<string>(pathComparer);

        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.FullName))
            {
                continue;
            }

            var relativePath = entry.FullName
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relativePath))
            {
                throw new InvalidOperationException(
                    $"7 Days to Die state package contains an absolute path: '{entry.FullName}'.");
            }

            var destinationPath = Path.GetFullPath(Path.Combine(fullStagingRoot, relativePath));
            if (!destinationPath.StartsWith(stagingPrefix, pathComparison))
            {
                throw new InvalidOperationException(
                    $"7 Days to Die state package contains a path outside the workspace root: '{entry.FullName}'.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            if (!extractedFiles.Add(destinationPath))
            {
                throw new InvalidOperationException(
                    $"7 Days to Die state package contains duplicate file path '{entry.FullName}'.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var sourceStream = entry.Open();
            await using var destinationStream = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                useAsync: true);
            await sourceStream.CopyToAsync(destinationStream, cancellationToken);
            await destinationStream.FlushAsync(cancellationToken);
        }
    }

    private static WorldIdentity ValidateSingleWorldBundle(string userDataRoot)
    {
        var fullRoot = Path.GetFullPath(userDataRoot);
        var savesRoot = Path.Combine(fullRoot, "Saves");
        if (!Directory.Exists(savesRoot))
        {
            throw new InvalidOperationException(
                "7 Days to Die state package has no Saves directory.");
        }

        var candidates = new List<WorldIdentity>();
        foreach (var worldDirectory in Directory.EnumerateDirectories(savesRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var worldName = Path.GetFileName(Path.TrimEndingDirectorySeparator(worldDirectory));
            if (string.IsNullOrWhiteSpace(worldName))
            {
                continue;
            }

            foreach (var saveDirectory in Directory.EnumerateDirectories(worldDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                if (!SevenDaysToDieWorldDiscovery.HasKnownWorldMarker(saveDirectory))
                {
                    continue;
                }

                var gameName = Path.GetFileName(Path.TrimEndingDirectorySeparator(saveDirectory));
                if (!string.IsNullOrWhiteSpace(gameName))
                {
                    candidates.Add(new WorldIdentity(worldName, gameName));
                }
            }
        }

        if (candidates.Count != 1)
        {
            throw new InvalidOperationException(
                $"7 Days to Die state package must contain exactly one World save; found {candidates.Count}.");
        }

        var identity = candidates[0];
        ValidateWorldBundle(
            fullRoot,
            identity.WorldName,
            identity.GameName,
            requireExclusiveBundle: true);
        return identity;
    }

    private static void ValidateWorldBundle(
        string userDataRoot,
        string worldName,
        string gameName,
        bool requireExclusiveBundle)
    {
        var saveRoot = Path.Combine(userDataRoot, "Saves", worldName, gameName);
        if (!Directory.Exists(saveRoot) ||
            !SevenDaysToDieWorldDiscovery.HasKnownWorldMarker(saveRoot))
        {
            throw new InvalidOperationException(
                $"7 Days to Die save '{gameName}' in World '{worldName}' has no recognized root World marker.");
        }

        if (!requireExclusiveBundle)
        {
            return;
        }

        var allowedTopLevelDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Saves",
            "GeneratedWorlds"
        };
        foreach (var directory in Directory.EnumerateDirectories(userDataRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (!allowedTopLevelDirectories.Contains(Path.GetFileName(directory)))
            {
                throw new InvalidOperationException(
                    $"7 Days to Die state package contains unexpected top-level directory '{Path.GetFileName(directory)}'.");
            }
        }

        if (Directory.EnumerateFiles(userDataRoot, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new InvalidOperationException(
                "7 Days to Die state package contains unexpected files at the workspace root.");
        }

        var savesRoot = Path.Combine(userDataRoot, "Saves");
        var worldDirectories = Directory.EnumerateDirectories(savesRoot, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (worldDirectories.Length != 1 ||
            !string.Equals(Path.GetFileName(worldDirectories[0]), worldName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "7 Days to Die state package contains saves for more than one GameWorld.");
        }

        var saveDirectories = Directory.EnumerateDirectories(worldDirectories[0], "*", SearchOption.TopDirectoryOnly).ToArray();
        if (saveDirectories.Length != 1 ||
            !string.Equals(Path.GetFileName(saveDirectories[0]), gameName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "7 Days to Die state package contains more than one GameName save.");
        }

        var generatedWorldsRoot = Path.Combine(userDataRoot, "GeneratedWorlds");
        if (Directory.Exists(generatedWorldsRoot))
        {
            if (Directory.EnumerateFiles(generatedWorldsRoot, "*", SearchOption.TopDirectoryOnly).Any())
            {
                throw new InvalidOperationException(
                    "7 Days to Die state package contains unexpected files directly under GeneratedWorlds.");
            }

            var generatedWorldDirectories = Directory
                .EnumerateDirectories(generatedWorldsRoot, "*", SearchOption.TopDirectoryOnly)
                .ToArray();
            if (generatedWorldDirectories.Length != 1 ||
                !string.Equals(
                    Path.GetFileName(generatedWorldDirectories[0]),
                    worldName,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "7 Days to Die state package contains generated terrain for a different or additional GameWorld.");
            }
        }
    }

    private static WorldIdentity GetDetectedWorldIdentity(
        string userDataRoot,
        string sourcePath)
    {
        var fullRoot = Path.GetFullPath(userDataRoot);
        var fullSource = Path.GetFullPath(sourcePath);
        var relativePath = Path.GetRelativePath(fullRoot, fullSource);
        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3 ||
            !string.Equals(segments[0], "Saves", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(segments[1]) ||
            string.IsNullOrWhiteSpace(segments[2]))
        {
            throw new InvalidOperationException(
                "The detected 7 Days to Die World is not beneath the installation's Saves/<GameWorld>/<GameName> root.");
        }

        return new WorldIdentity(segments[1], segments[2]);
    }

    private static string GetRequiredUserDataRoot(GameInstallation installation)
    {
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(SevenDaysToDieInstallationDiscovery.UserDataPathKey, out var userDataPath) ||
            string.IsNullOrWhiteSpace(userDataPath))
        {
            throw new InvalidOperationException(
                "7 Days to Die installation is missing its user-data path.");
        }

        return Path.GetFullPath(userDataPath);
    }

    private static string GetLocalWorkRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.GetTempPath();
        }

        var root = Path.Combine(localData, "Steward", "workspaces", "7-days-to-die");
        Directory.CreateDirectory(root);
        return root;
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

    private sealed record WorldIdentity(string WorldName, string GameName);
}
