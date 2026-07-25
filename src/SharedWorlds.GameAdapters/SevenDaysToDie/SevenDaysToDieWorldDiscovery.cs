using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal static class SevenDaysToDieWorldDiscovery
{
    private const string SavesDirectoryName = "Saves";
    private static readonly string[] MainWorldFileNames = ["main.ttp", "main.ttw"];

    public static IReadOnlyList<DetectedWorld> Discover(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(SevenDaysToDieInstallationDiscovery.UserDataPathKey, out var userDataPath) ||
            string.IsNullOrWhiteSpace(userDataPath) ||
            !IsSafeDiscoveryDirectory(userDataPath))
        {
            return [];
        }

        var savesRoot = Path.Combine(userDataPath, SavesDirectoryName);
        if (!IsSafeDiscoveryDirectory(savesRoot))
        {
            return [];
        }

        var worlds = new List<DetectedWorld>();
        foreach (var worldDirectory in EnumerateDirectoriesSafely(savesRoot))
        {
            if (!IsSafeDiscoveryDirectory(worldDirectory))
            {
                continue;
            }

            var worldName = Path.GetFileName(Path.TrimEndingDirectorySeparator(worldDirectory));
            if (string.IsNullOrWhiteSpace(worldName))
            {
                continue;
            }

            foreach (var saveDirectory in EnumerateDirectoriesSafely(worldDirectory))
            {
                if (!IsSafeDiscoveryDirectory(saveDirectory) ||
                    !HasKnownWorldMarker(saveDirectory))
                {
                    continue;
                }

                var saveName = Path.GetFileName(Path.TrimEndingDirectorySeparator(saveDirectory));
                if (string.IsNullOrWhiteSpace(saveName))
                {
                    continue;
                }

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(saveDirectory);
                }
                catch (Exception exception) when (
                    exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }

                worlds.Add(new DetectedWorld(
                    Id: fullPath,
                    DisplayName: $"{saveName} ({worldName})",
                    SourcePath: fullPath));
            }
        }

        return worlds
            .OrderByDescending(world => GetWorldLastWriteTimeUtc(world.SourcePath))
            .ThenBy(world => world.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool HasKnownWorldMarker(string saveDirectory)
        => MainWorldFileNames.Any(name => IsSafeDiscoveryFile(Path.Combine(saveDirectory, name)));

    private static DateTime GetWorldLastWriteTimeUtc(string saveDirectory)
        => MainWorldFileNames
            .Select(name => GetLastWriteTimeUtcSafe(Path.Combine(saveDirectory, name)))
            .Max();

    private static bool IsSafeDiscoveryDirectory(string path)
        => TryGetSafeDiscoveryAttributes(path, out var attributes) &&
           (attributes & FileAttributes.Directory) != 0;

    private static bool IsSafeDiscoveryFile(string path)
        => TryGetSafeDiscoveryAttributes(path, out var attributes) &&
           (attributes & FileAttributes.Directory) == 0;

    private static bool TryGetSafeDiscoveryAttributes(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            attributes = default;
            return false;
        }

        return (attributes & FileAttributes.ReparsePoint) == 0;
    }

    private static IEnumerable<string> EnumerateDirectoriesSafely(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static DateTime GetLastWriteTimeUtcSafe(string path)
    {
        if (!IsSafeDiscoveryFile(path))
        {
            return DateTime.MinValue;
        }

        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            ArgumentException)
        {
            return DateTime.MinValue;
        }
    }
}
