using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal static class ProjectZomboidWorldDiscovery
{
    private const string MapTimeFileName = "map_t.bin";

    public static IReadOnlyList<DetectedWorld> Discover(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(ProjectZomboidInstallationDiscovery.UserDataPathKey, out var userDataPath) ||
            string.IsNullOrWhiteSpace(userDataPath))
        {
            return [];
        }

        var multiplayerRoot = Path.Combine(userDataPath, "Saves", "Multiplayer");
        if (!Directory.Exists(multiplayerRoot))
        {
            return [];
        }

        var worlds = new List<DetectedWorld>();
        foreach (var directory in EnumerateDirectoriesSafely(multiplayerRoot))
        {
            var mapTimePath = Path.Combine(directory, MapTimeFileName);
            if (!File.Exists(mapTimePath))
            {
                continue;
            }

            var serverName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
            if (string.IsNullOrWhiteSpace(serverName))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(directory);
            worlds.Add(new DetectedWorld(
                Id: fullPath,
                DisplayName: serverName,
                SourcePath: fullPath));
        }

        return worlds
            .OrderByDescending(world => GetLastWriteTimeUtcSafe(Path.Combine(world.SourcePath, MapTimeFileName)))
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
