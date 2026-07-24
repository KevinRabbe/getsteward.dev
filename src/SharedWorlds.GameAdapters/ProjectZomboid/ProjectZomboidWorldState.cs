using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal static class ProjectZomboidWorldState
{
    private const string MapTimeFileName = "map_t.bin";
    private static readonly string[] ServerConfigSuffixes =
    [
        ".ini",
        "_SandboxVars.lua",
        "_spawnpoints.lua",
        "_spawnregions.lua"
    ];

    public static Task<CapturedState> CaptureDetectedWorldAsync(
        GameInstallation installation,
        DetectedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(world);
        var userDataRoot = GetRequiredUserDataRoot(installation);
        var serverName = GetServerName(world.SourcePath);
        var expectedWorldPath = Path.GetFullPath(Path.Combine(
            userDataRoot,
            "Saves",
            "Multiplayer",
            serverName));
        var actualWorldPath = Path.GetFullPath(world.SourcePath);
        if (!PathsEqual(expectedWorldPath, actualWorldPath))
        {
            throw new InvalidOperationException(
                "The detected Project Zomboid World is not beneath the installation's authoritative multiplayer save root.");
        }

        return CaptureServerBundleAsync(userDataRoot, serverName, cancellationToken);
    }

    public static Task<CapturedState> CapturePreparedWorldAsync(
        PreparedWorld world,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        var userDataRoot = Path.GetFullPath(world.WorkingDirectory);
        var serverName = ValidateSingleServerBundle(userDataRoot);
        return CaptureServerBundleAsync(userDataRoot, serverName, cancellationToken);
    }

    public static PreparedWorld PrepareEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(requiredEnvironment);
        var verification = ProjectZomboidEnvironment.Verify(installation, requiredEnvironment);
        if (!verification.IsReady)
        {
            throw new EnvironmentReproductionException(
                "project-zomboid",
                string.Join("; ", verification.Issues.Select(issue => issue.Message)));
        }

        var operationRoot = Path.Combine(
            GetLocalWorkRoot(),
            Guid.NewGuid().ToString("N"));
        var userDataRoot = Path.Combine(operationRoot, "Zomboid");
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
                "The Project Zomboid state package does not exist.",
                packagePath);
        }

        var destinationRoot = Path.GetFullPath(world.WorkingDirectory);
        var parentRoot = Path.GetDirectoryName(destinationRoot)
            ?? throw new InvalidOperationException(
                $"Could not determine the parent directory for Project Zomboid workspace '{destinationRoot}'.");
        Directory.CreateDirectory(parentRoot);

        var operationId = Guid.NewGuid().ToString("N");
        var stagingRoot = destinationRoot + ".sharedworlds-staging-" + operationId;
        var rollbackRoot = destinationRoot + ".sharedworlds-rollback-" + operationId;
        var movedExisting = false;

        try
        {
            await ExtractPackageAsync(packagePath, stagingRoot, cancellationToken);
            ValidateSingleServerBundle(stagingRoot);

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
                        "Project Zomboid state restore failed and the previous prepared server state could not be rolled back automatically.",
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

    private static async Task<CapturedState> CaptureServerBundleAsync(
        string userDataRoot,
        string serverName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullUserDataRoot = Path.GetFullPath(userDataRoot);
        ValidateServerBundle(fullUserDataRoot, serverName, requireExclusiveBundle: false);

        var packagePath = Path.Combine(
            Path.GetTempPath(),
            $"sharedworlds-project-zomboid-{Guid.NewGuid():N}.zip");
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

            var worldRoot = Path.Combine(fullUserDataRoot, "Saves", "Multiplayer", serverName);
            foreach (var filePath in Directory.EnumerateFiles(worldRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativeToWorld = Path.GetRelativePath(worldRoot, filePath);
                await AddFileAsync(
                    archive,
                    filePath,
                    Path.Combine("Saves", "Multiplayer", serverName, relativeToWorld),
                    cancellationToken);
            }

            var serverRoot = Path.Combine(fullUserDataRoot, "Server");
            foreach (var suffix in ServerConfigSuffixes)
            {
                var configPath = Path.Combine(serverRoot, serverName + suffix);
                if (File.Exists(configPath))
                {
                    await AddFileAsync(
                        archive,
                        configPath,
                        Path.Combine("Server", serverName + suffix),
                        cancellationToken);
                }
            }

            var databasePath = Path.Combine(fullUserDataRoot, "db", serverName + ".db");
            if (File.Exists(databasePath))
            {
                await AddFileAsync(
                    archive,
                    databasePath,
                    Path.Combine("db", serverName + ".db"),
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

    private static async Task AddFileAsync(
        ZipArchive archive,
        string sourcePath,
        string entryPath,
        CancellationToken cancellationToken)
    {
        var entryName = entryPath.Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
        {
            entryName = entryName.Replace(Path.AltDirectorySeparatorChar, '/');
        }

        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var sourceStream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            useAsync: true);
        await using var destinationStream = entry.Open();
        await sourceStream.CopyToAsync(destinationStream, cancellationToken);
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
                    $"Project Zomboid state package contains an absolute path: '{entry.FullName}'.");
            }

            var destinationPath = Path.GetFullPath(Path.Combine(fullStagingRoot, relativePath));
            if (!destinationPath.StartsWith(stagingPrefix, pathComparison))
            {
                throw new InvalidOperationException(
                    $"Project Zomboid state package contains a path outside the workspace root: '{entry.FullName}'.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            if (!extractedFiles.Add(destinationPath))
            {
                throw new InvalidOperationException(
                    $"Project Zomboid state package contains duplicate file path '{entry.FullName}'.");
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

    private static string ValidateSingleServerBundle(string userDataRoot)
    {
        var fullRoot = Path.GetFullPath(userDataRoot);
        var multiplayerRoot = Path.Combine(fullRoot, "Saves", "Multiplayer");
        if (!Directory.Exists(multiplayerRoot))
        {
            throw new InvalidOperationException(
                "Project Zomboid state package has no Saves/Multiplayer directory.");
        }

        var candidateWorlds = Directory
            .EnumerateDirectories(multiplayerRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => File.Exists(Path.Combine(path, MapTimeFileName)))
            .ToArray();
        if (candidateWorlds.Length != 1)
        {
            throw new InvalidOperationException(
                $"Project Zomboid state package must contain exactly one authoritative multiplayer World; found {candidateWorlds.Length}.");
        }

        var serverName = GetServerName(candidateWorlds[0]);
        ValidateServerBundle(fullRoot, serverName, requireExclusiveBundle: true);
        return serverName;
    }

    private static void ValidateServerBundle(
        string userDataRoot,
        string serverName,
        bool requireExclusiveBundle)
    {
        var worldRoot = Path.Combine(userDataRoot, "Saves", "Multiplayer", serverName);
        if (!Directory.Exists(worldRoot) ||
            !File.Exists(Path.Combine(worldRoot, MapTimeFileName)))
        {
            throw new InvalidOperationException(
                $"Project Zomboid server '{serverName}' has no authoritative multiplayer World with {MapTimeFileName}.");
        }

        var serverRoot = Path.Combine(userDataRoot, "Server");
        var mainConfigPath = Path.Combine(serverRoot, serverName + ".ini");
        if (!File.Exists(mainConfigPath))
        {
            throw new InvalidOperationException(
                $"Project Zomboid server '{serverName}' has no matching Server/{serverName}.ini definition.");
        }

        if (!requireExclusiveBundle)
        {
            return;
        }

        var allowedTopLevelDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Saves",
            "Server",
            "db"
        };
        foreach (var directory in Directory.EnumerateDirectories(userDataRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (!allowedTopLevelDirectories.Contains(Path.GetFileName(directory)))
            {
                throw new InvalidOperationException(
                    $"Project Zomboid state package contains unexpected top-level directory '{Path.GetFileName(directory)}'.");
            }
        }

        if (Directory.EnumerateFiles(userDataRoot, "*", SearchOption.TopDirectoryOnly).Any())
        {
            throw new InvalidOperationException(
                "Project Zomboid state package contains unexpected files at the workspace root.");
        }

        var savesRoot = Path.Combine(userDataRoot, "Saves");
        var savesDirectories = Directory.Exists(savesRoot)
            ? Directory.EnumerateDirectories(savesRoot, "*", SearchOption.TopDirectoryOnly).ToArray()
            : [];
        if (savesDirectories.Length != 1 ||
            !string.Equals(Path.GetFileName(savesDirectories[0]), "Multiplayer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Project Zomboid state package may contain only Saves/Multiplayer state.");
        }

        var multiplayerDirectories = Directory
            .EnumerateDirectories(Path.Combine(savesRoot, "Multiplayer"), "*", SearchOption.TopDirectoryOnly)
            .ToArray();
        if (multiplayerDirectories.Length != 1 ||
            !string.Equals(Path.GetFileName(multiplayerDirectories[0]), serverName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Project Zomboid state package contains state for more than one multiplayer server.");
        }

        if (Directory.Exists(serverRoot))
        {
            var allowedServerFiles = ServerConfigSuffixes
                .Select(suffix => serverName + suffix)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var filePath in Directory.EnumerateFiles(serverRoot, "*", SearchOption.AllDirectories))
            {
                if (!PathsEqual(Path.GetDirectoryName(filePath)!, serverRoot) ||
                    !allowedServerFiles.Contains(Path.GetFileName(filePath)))
                {
                    throw new InvalidOperationException(
                        $"Project Zomboid state package contains unexpected server configuration '{Path.GetRelativePath(userDataRoot, filePath)}'.");
                }
            }
        }

        var dbRoot = Path.Combine(userDataRoot, "db");
        if (Directory.Exists(dbRoot))
        {
            var expectedDatabaseName = serverName + ".db";
            foreach (var filePath in Directory.EnumerateFiles(dbRoot, "*", SearchOption.AllDirectories))
            {
                if (!PathsEqual(Path.GetDirectoryName(filePath)!, dbRoot) ||
                    !string.Equals(Path.GetFileName(filePath), expectedDatabaseName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Project Zomboid state package contains unexpected database file '{Path.GetRelativePath(userDataRoot, filePath)}'.");
                }
            }
        }
    }

    private static string GetRequiredUserDataRoot(GameInstallation installation)
    {
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(ProjectZomboidInstallationDiscovery.UserDataPathKey, out var userDataPath) ||
            string.IsNullOrWhiteSpace(userDataPath))
        {
            throw new InvalidOperationException(
                "Project Zomboid installation is missing its user-data path.");
        }

        return Path.GetFullPath(userDataPath);
    }

    private static string GetServerName(string worldPath)
    {
        var serverName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(worldPath)));
        if (string.IsNullOrWhiteSpace(serverName) ||
            serverName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(
                $"Could not determine a safe Project Zomboid server name from '{worldPath}'.");
        }

        return serverName;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string GetLocalWorkRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            localData = Path.GetTempPath();
        }

        var root = Path.Combine(localData, "Steward", "workspaces", "project-zomboid");
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
}
