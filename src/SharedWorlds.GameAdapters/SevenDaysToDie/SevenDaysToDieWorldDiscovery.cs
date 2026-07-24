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
                if (!HasKnownWorldMarker(saveDirectory))
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
            .OrderByDescending(world => GetWorldLastWriteTimeUtc(world.SourcePath))
            .ThenBy(world => world.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool HasKnownWorldMarker(string saveDirectory)
        => MainWorldFileNames.Any(name => File.Exists(Path.Combine(saveDirectory, name)));

    private static DateTime GetWorldLastWriteTimeUtc(string saveDirectory)
        => MainWorldFileNames
            .Select(name => GetLastWriteTimeUtcSafe(Path.Combine(saveDirectory, name)))
            .Max();

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
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
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
