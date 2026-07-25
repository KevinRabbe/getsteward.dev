using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.ConanExilesEnhanced;

internal static class ConanExilesEnhancedWorldDiscovery
{
    internal const int SaveSlotCount = 10;

    public static IReadOnlyList<DetectedWorld> Discover(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                ConanExilesEnhancedInstallationDiscovery.SaveGamesRootPathKey,
                out var saveRoot) ||
            string.IsNullOrWhiteSpace(saveRoot))
        {
            return [];
        }

        return DiscoverFromSavedRoot(saveRoot);
    }

    internal static IReadOnlyList<DetectedWorld> DiscoverFromSavedRoot(string saveRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveRoot);
        var fullRoot = Path.GetFullPath(saveRoot);
        if (!IsRegularDirectory(fullRoot))
        {
            return [];
        }

        var worlds = new List<DetectedWorld>();
        for (var slot = 0; slot < SaveSlotCount; slot++)
        {
            var path = Path.Combine(fullRoot, GetSaveFileName(slot));
            if (!IsSafelyCapturableDatabase(path))
            {
                continue;
            }

            worlds.Add(new DetectedWorld(
                Id: $"local:slot-{slot}",
                DisplayName: $"Save slot {slot + 1}",
                SourcePath: Path.GetFullPath(path)));
        }

        return worlds
            .OrderByDescending(world => GetWriteTimeUtcSafe(world.SourcePath))
            .ThenBy(world => world.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool IsSupportedSavePath(string path)
    {
        var fileName = Path.GetFileName(path);
        for (var slot = 0; slot < SaveSlotCount; slot++)
        {
            if (string.Equals(fileName, GetSaveFileName(slot), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsSafelyCapturableDatabase(string path)
    {
        if (!IsSupportedSavePath(path) || !IsRegularNonEmptyFile(path))
        {
            return false;
        }

        return !File.Exists(path + "-wal") &&
               !File.Exists(path + "-shm") &&
               !File.Exists(path + "-journal");
    }

    internal static string GetSaveFileName(int slot)
    {
        if (slot is < 0 or >= SaveSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        return $"game_{slot}.db";
    }

    internal static bool IsRegularNonEmptyFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            return new FileInfo(path).Length > 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsRegularDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0 &&
                   (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static DateTime GetWriteTimeUtcSafe(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
