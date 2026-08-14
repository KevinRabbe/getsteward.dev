using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal static partial class SevenDaysToDieWorldState
{
    private static readonly string[] PreparedPayloadDirectoryNames =
    [
        "Saves",
        "GeneratedWorlds"
    ];

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

        return new PreparedWorld(
            installation,
            SevenDaysToDieWorkspaceOwnership.Create(),
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

        var destinationRoot = SevenDaysToDieWorkspaceOwnership.RequireOwned(
            world.WorkingDirectory);
        var packagePath = Path.GetFullPath(state.Path);
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException(
                "The 7 Days to Die state package does not exist.",
                packagePath);
        }

        var operationId = Guid.NewGuid().ToString("N");
        var stagingRoot = Path.Combine(
            destinationRoot,
            $".sharedworlds-staging-{operationId}");
        var rollbackRoot = Path.Combine(
            destinationRoot,
            $".sharedworlds-rollback-{operationId}");
        var movedExisting = new List<string>();
        var promoted = new List<string>();

        try
        {
            await ExtractPackageAsync(packagePath, stagingRoot, cancellationToken);
            ValidateSingleWorldBundle(stagingRoot);

            Directory.CreateDirectory(rollbackRoot);
            foreach (var name in PreparedPayloadDirectoryNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = Path.Combine(destinationRoot, name);
                if (File.Exists(current))
                {
                    throw new InvalidOperationException(
                        $"Prepared 7 Days to Die workspace contains a file where payload directory '{name}' is required.");
                }

                if (!Directory.Exists(current))
                {
                    continue;
                }

                Directory.Move(current, Path.Combine(rollbackRoot, name));
                movedExisting.Add(name);
            }

            foreach (var name in PreparedPayloadDirectoryNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var staged = Path.Combine(stagingRoot, name);
                if (!Directory.Exists(staged))
                {
                    continue;
                }

                Directory.Move(staged, Path.Combine(destinationRoot, name));
                promoted.Add(name);
            }

            TryDeleteDirectory(stagingRoot);
            TryDeleteDirectory(rollbackRoot);
        }
        catch (Exception restoreException)
        {
            Exception? rollbackException = null;
            try
            {
                foreach (var name in promoted.AsEnumerable().Reverse())
                {
                    var promotedPath = Path.Combine(destinationRoot, name);
                    if (Directory.Exists(promotedPath))
                    {
                        Directory.Delete(promotedPath, recursive: true);
                    }
                }

                foreach (var name in movedExisting.AsEnumerable().Reverse())
                {
                    var saved = Path.Combine(rollbackRoot, name);
                    var original = Path.Combine(destinationRoot, name);
                    if (Directory.Exists(saved) &&
                        !Directory.Exists(original) &&
                        !File.Exists(original))
                    {
                        Directory.Move(saved, original);
                    }
                }
            }
            catch (Exception exception)
            {
                rollbackException = exception;
            }

            TryDeleteDirectory(stagingRoot);
            TryDeleteDirectory(rollbackRoot);

            if (rollbackException is not null)
            {
                throw new AggregateException(
                    "7 Days to Die state restore failed and the previous prepared World could not be rolled back automatically.",
                    restoreException,
                    rollbackException);
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
        _ = SevenDaysToDieWorkspaceOwnership.RequireOwned(world.WorkingDirectory);

        if (disposition == PreparedWorldDisposition.Discard)
        {
            SevenDaysToDieWorkspaceOwnership.DeleteOwned(world.WorkingDirectory);
        }

        return Task.CompletedTask;
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