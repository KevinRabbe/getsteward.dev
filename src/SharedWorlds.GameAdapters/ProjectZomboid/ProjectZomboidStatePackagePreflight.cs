using System.IO.Compression;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal static class ProjectZomboidStatePackagePreflight
{
    private const int MaxArchiveEntries = 1_000_000;
    private const long MinimumFreeSpaceReserveBytes = 256L * 1024 * 1024;

    public static void Validate(string packagePath, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        using var archive = ZipFile.OpenRead(Path.GetFullPath(packagePath));
        var declaredBytes = GetDeclaredExtractionBytes(
            archive,
            MaxArchiveEntries);
        EnsureSufficientFreeSpace(
            workingDirectory,
            declaredBytes,
            MinimumFreeSpaceReserveBytes);
    }

    internal static long GetDeclaredExtractionBytes(
        ZipArchive archive,
        int maxEntries)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        }

        if (archive.Entries.Count > maxEntries)
        {
            throw new InvalidDataException(
                $"Project Zomboid state package contains more than {maxEntries} archive entries; Steward stopped before extraction.");
        }

        long total = 0;
        try
        {
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                total = checked(total + entry.Length);
            }
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "Project Zomboid state package declares an impossible total extraction size.",
                exception);
        }

        return total;
    }

    internal static void EnsureSufficientFreeSpace(
        string workingDirectory,
        long declaredBytes,
        long reserveBytes,
        long? availableBytesOverride = null)
    {
        if (declaredBytes < 0 || reserveBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(declaredBytes));
        }

        long requiredBytes;
        try
        {
            requiredBytes = checked(declaredBytes + reserveBytes);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "Project Zomboid state package declares an impossible extraction size.",
                exception);
        }

        long? availableBytes = availableBytesOverride;
        if (availableBytes is null)
        {
            var root = Path.GetPathRoot(Path.GetFullPath(workingDirectory));
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            try
            {
                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    return;
                }

                availableBytes = drive.AvailableFreeSpace;
            }
            catch (DriveNotFoundException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }
        }

        if (availableBytes.Value < requiredBytes)
        {
            throw new IOException(
                $"Project Zomboid state package requires {requiredBytes} bytes of free space for safe extraction, but only {availableBytes.Value} bytes are available.");
        }
    }
}
