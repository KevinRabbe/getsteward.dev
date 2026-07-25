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
            string.IsNullOrWhiteSpace(userDataPath) ||
            !IsSafeDiscoveryDirectory(userDataPath))
        {
            return [];
        }

        var multiplayerRoot = Path.Combine(userDataPath, "Saves", "Multiplayer");
        var serverConfigRoot = Path.Combine(userDataPath, "Server");
        if (!IsSafeDiscoveryDirectory(multiplayerRoot) ||
            !IsSafeDiscoveryDirectory(serverConfigRoot))
        {
            return [];
        }

        var worlds = new List<DetectedWorld>();
        foreach (var directory in EnumerateDirectoriesSafely(multiplayerRoot))
        {
            if (!IsSafeDiscoveryDirectory(directory))
            {
                continue;
            }

            var mapTimePath = Path.Combine(directory, MapTimeFileName);
            if (!IsSafeDiscoveryFile(mapTimePath))
            {
                continue;
            }

            var serverName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
            if (string.IsNullOrWhiteSpace(serverName))
            {
                continue;
            }

            // Saves/Multiplayer also contains client-side caches downloaded from remote servers.
            // A World is authoritative on this device only when the same server instance has a
            // local server definition. Steward ignores remote caches instead of guessing from their
            // internal save contents.
            var serverConfigPath = Path.Combine(serverConfigRoot, serverName + ".ini");
            if (!IsSafeDiscoveryFile(serverConfigPath))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(directory);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

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
