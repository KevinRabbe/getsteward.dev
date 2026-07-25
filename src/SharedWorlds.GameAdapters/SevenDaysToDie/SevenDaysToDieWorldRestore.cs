using System.IO.Compression;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Errors;

namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal static partial class SevenDaysToDieWorldState
{
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
}
