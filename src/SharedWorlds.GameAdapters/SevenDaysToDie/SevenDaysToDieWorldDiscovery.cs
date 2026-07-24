using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal static class SevenDaysToDieWorldDiscovery
{
    private const string SavesDirectoryName = "Saves";
    private const string MainWorldFileName = "main.ttw";

    public static IReadOnlyList<DetectedWorld> Discover(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(SevenDaysToDieInstallationDiscovery.UserDataPathKey, out var userDataPath) ||
            string.IsNullOrWhiteSpace(userDataPath))
        {
            return [];
        }

        var savesRoot = Path.Combine(userDataPath, SavesDirectoryName);
        if (!Directory.Exists(savesRoot))
        {
            return [];
        }

        var worlds = new List<DetectedWorld>();
        foreach (var worldDirectory in EnumerateDirectoriesSafely(savesRoot))
        {
            var worldName = Path.GetFileName(Path.TrimEndingDirectorySeparator(worldDirectory));
            if (string.IsNullOrWhiteSpace(worldName))
            {
                continue;
            }

            foreach (var saveDirectory in EnumerateDirectoriesSafely(worldDirectory))
            {
                var mainWorldPath = Path.Combine(saveDirectory, MainWorldFileName);
                if (!File.Exists(mainWorldPath))
                {
                    continue;
                }

                var saveName = Path.GetFileName(Path.TrimEndingDirectorySeparator(saveDirectory));
                if (string.IsNullOrWhiteSpace(saveName))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(saveDirectory);
                worlds.Add(new DetectedWorld(
                    Id: fullPath,
                    DisplayName: $"{saveName} ({worldName})",
                    SourcePath: fullPath));
            }
        }

        return worlds
            .OrderByDescending(world => GetLastWriteTimeUtcSafe(Path.Combine(world.SourcePath, MainWorldFileName)))
            .ThenBy(world => world.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
