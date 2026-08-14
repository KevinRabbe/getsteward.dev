using System.Text;
using System.Threading;

namespace SharedWorlds.Desktop;

/// <summary>
/// Resolves SafeWorld's one durable per-user data root. Migration is only valid after the desktop
/// single-instance boundary has made this process primary, before any storage/runtime writer exists.
/// </summary>
internal static class DesktopLocalDataRoot
{
    internal const string CurrentDirectoryName = "SafeWorld";
    internal const string LegacyDirectoryName = "SharedWorlds";

    private const string LegacyGuardText = "safeworld-local-data-root-migrated:v1";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static string? _resolvedRoot;

    internal static string ResolveAndMigrateForCurrentUser()
    {
        var localApplicationDataRoot = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationDataRoot))
        {
            localApplicationDataRoot = Path.GetTempPath();
        }

        var resolvedRoot = ResolveAndMigrate(localApplicationDataRoot);
        Volatile.Write(ref _resolvedRoot, resolvedRoot);
        return resolvedRoot;
    }

    internal static string ResolveAndMigrate(string localApplicationDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataRoot);

        var parentRoot = Path.GetFullPath(localApplicationDataRoot);
        var currentRoot = Path.Combine(parentRoot, CurrentDirectoryName);
        var legacyRoot = Path.Combine(parentRoot, LegacyDirectoryName);

        if (File.Exists(currentRoot))
        {
            throw new InvalidDataException(
                $"SafeWorld's local data path '{currentRoot}' is a file, not a directory. " +
                "SafeWorld stopped before opening local storage.");
        }

        var currentExists = Directory.Exists(currentRoot);
        var legacyDirectoryExists = Directory.Exists(legacyRoot);
        var legacyFileExists = File.Exists(legacyRoot);

        if (currentExists)
        {
            EnsurePlainDirectory(currentRoot, "current");
        }

        if (legacyDirectoryExists)
        {
            EnsurePlainDirectory(legacyRoot, "legacy");
        }

        if (legacyFileExists)
        {
            if (!IsRecognizedLegacyGuard(legacyRoot))
            {
                throw new InvalidDataException(
                    $"SafeWorld found an unexpected file at its legacy local data path '{legacyRoot}'. " +
                    "SafeWorld stopped rather than overwrite it or guess which store is authoritative.");
            }

            if (!currentExists)
            {
                throw new InvalidDataException(
                    $"SafeWorld found its legacy migration guard at '{legacyRoot}', but the migrated " +
                    $"local data directory '{currentRoot}' is missing. SafeWorld stopped rather than " +
                    "create an empty replacement store.");
            }

            return currentRoot;
        }

        if (legacyDirectoryExists && currentExists)
        {
            throw new InvalidDataException(
                $"SafeWorld found both legacy '{legacyRoot}' and current '{currentRoot}' local data " +
                "directories. SafeWorld stopped rather than merge, overwrite, or choose between two " +
                "possible authority stores.");
        }

        if (legacyDirectoryExists)
        {
            return MigrateLegacyRoot(parentRoot, legacyRoot, currentRoot);
        }

        var createdCurrentRoot = false;
        if (!currentExists)
        {
            Directory.CreateDirectory(currentRoot);
            createdCurrentRoot = true;
        }

        try
        {
            InstallLegacyGuard(parentRoot, legacyRoot);
        }
        catch
        {
            if (createdCurrentRoot)
            {
                TryDeleteEmptyDirectory(currentRoot);
            }

            throw;
        }

        return currentRoot;
    }

    internal static string RequireResolvedRoot()
        => Volatile.Read(ref _resolvedRoot)
           ?? throw new InvalidOperationException(
               "SafeWorld local data was requested before primary-process migration completed.");

    internal static string GetDiagnosticsRoot()
    {
        var resolvedRoot = Volatile.Read(ref _resolvedRoot);
        return resolvedRoot is null
            ? Path.Combine(Path.GetTempPath(), "SafeWorld", "startup-logs")
            : Path.Combine(resolvedRoot, "logs");
    }

    private static string MigrateLegacyRoot(
        string parentRoot,
        string legacyRoot,
        string currentRoot)
    {
        var preparedGuard = PrepareLegacyGuard(parentRoot);
        try
        {
            // Both roots are siblings under LocalAppData. Do not copy user data: one directory move
            // either publishes the existing store at the SafeWorld path or throws before startup.
            Directory.Move(legacyRoot, currentRoot);

            try
            {
                File.Move(preparedGuard, legacyRoot);
            }
            catch (Exception guardException) when (
                guardException is IOException or UnauthorizedAccessException)
            {
                Exception? rollbackException = null;
                var restoredLegacyRoot = false;
                try
                {
                    if (!File.Exists(legacyRoot) &&
                        !Directory.Exists(legacyRoot) &&
                        Directory.Exists(currentRoot))
                    {
                        Directory.Move(currentRoot, legacyRoot);
                        restoredLegacyRoot = true;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    rollbackException = exception;
                }

                if (!restoredLegacyRoot)
                {
                    var inner = rollbackException is null
                        ? guardException
                        : new AggregateException(guardException, rollbackException);
                    throw new IOException(
                        "SafeWorld moved the legacy local data directory but could not publish its " +
                        "legacy-path downgrade guard or safely restore the old directory name. " +
                        "SafeWorld stopped before constructing local storage.",
                        inner);
                }

                throw new IOException(
                    "SafeWorld could not publish its legacy-path downgrade guard. The legacy local " +
                    "data directory was restored and SafeWorld stopped before constructing storage.",
                    guardException);
            }

            return currentRoot;
        }
        finally
        {
            TryDeleteFile(preparedGuard);
        }
    }

    private static void InstallLegacyGuard(string parentRoot, string legacyRoot)
    {
        var preparedGuard = PrepareLegacyGuard(parentRoot);
        try
        {
            if (File.Exists(legacyRoot) || Directory.Exists(legacyRoot))
            {
                throw new InvalidDataException(
                    $"SafeWorld's legacy local data path '{legacyRoot}' became occupied while the " +
                    "migration guard was being prepared. SafeWorld stopped before opening storage.");
            }

            File.Move(preparedGuard, legacyRoot);
        }
        finally
        {
            TryDeleteFile(preparedGuard);
        }
    }

    private static string PrepareLegacyGuard(string parentRoot)
    {
        var path = Path.Combine(
            parentRoot,
            $".SafeWorld.SharedWorlds.guard.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, LegacyGuardText, Utf8NoBom);
        return path;
    }

    private static bool IsRecognizedLegacyGuard(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 256)
        {
            return false;
        }

        return string.Equals(
            File.ReadAllText(path, Utf8NoBom),
            LegacyGuardText,
            StringComparison.Ordinal);
    }

    private static void EnsurePlainDirectory(string path, string role)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                $"SafeWorld's {role} local data directory '{path}' is a reparse point. SafeWorld " +
                "stopped rather than migrate or write through an unexpected filesystem redirect.");
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

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
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
