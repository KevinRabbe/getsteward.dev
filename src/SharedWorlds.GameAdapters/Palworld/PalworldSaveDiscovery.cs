using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Palworld;

internal static class PalworldSaveDiscovery
{
    private const string LevelSaveFileName = "Level.sav";
    private const string PlayersDirectoryName = "Players";
    private const string SharedWorldsBackupMarker = ".sharedworlds-backup";
    private const string SharedWorldsStagingMarker = ".sharedworlds-staging-";
    private const string SharedWorldsRollbackMarker = ".sharedworlds-rollback-";

    public static IReadOnlyList<DetectedWorld> Discover(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var seenPaths = new HashSet<string>(comparer);
        var worlds = new List<DetectedWorld>();

        foreach (var root in GetCandidateSaveRoots(installation))
        {
            DiscoverUnderRoot(root.Path, root.Kind, worlds, seenPaths);
        }

        return worlds
            .OrderByDescending(world => GetLastWriteTimeUtcSafe(Path.Combine(world.SourcePath, LevelSaveFileName)))
            .ThenBy(world => world.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<PalworldSaveRoot> GetCandidateSaveRoots(GameInstallation installation)
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                yield return new PalworldSaveRoot(
                    Path.Combine(localAppData, "Pal", "Saved", "SaveGames"),
                    "local");
            }
        }

        if (installation.Metadata is not null &&
            installation.Metadata.TryGetValue(
                PalworldInstallationDiscovery.DedicatedServerRootPathKey,
                out var serverRoot) &&
            !string.IsNullOrWhiteSpace(serverRoot) &&
            IsSafeDiscoveryDirectory(serverRoot))
        {
            yield return new PalworldSaveRoot(
                Path.Combine(serverRoot, "Pal", "Saved", "SaveGames"),
                "dedicated");
        }
    }

    private static void DiscoverUnderRoot(
        string saveGamesRoot,
        string kind,
        ICollection<DetectedWorld> worlds,
        ISet<string> seenPaths)
    {
        if (!IsSafeDiscoveryDirectory(saveGamesRoot))
        {
            return;
        }

        foreach (var profileDirectory in EnumerateDirectoriesSafe(saveGamesRoot))
        {
            if (!IsSafeDiscoveryDirectory(profileDirectory))
            {
                continue;
            }

            var profileId = Path.GetFileName(profileDirectory);
            foreach (var worldDirectory in EnumerateDirectoriesSafe(profileDirectory))
            {
                var worldId = Path.GetFileName(worldDirectory);
                if (IsSharedWorldsInternalDirectoryName(worldId) ||
                    !IsSafeDiscoveryDirectory(worldDirectory))
                {
                    continue;
                }

                var levelSavePath = Path.Combine(worldDirectory, LevelSaveFileName);
                if (!IsSafeDiscoveryFile(levelSavePath))
                {
                    continue;
                }

                string normalizedPath;
                try
                {
                    normalizedPath = Path.GetFullPath(worldDirectory);
                }
                catch (Exception exception) when (
                    exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }

                if (!seenPaths.Add(normalizedPath))
                {
                    continue;
                }

                var shortWorldId = worldId.Length <= 8 ? worldId : worldId[..8];
                var hasPlayers = Directory.Exists(Path.Combine(worldDirectory, PlayersDirectoryName));
                var displaySuffix = hasPlayers ? string.Empty : " • no Players folder";

                worlds.Add(new DetectedWorld(
                    Id: $"{kind}:{profileId}:{worldId}",
                    DisplayName: $"Palworld {shortWorldId} ({kind}){displaySuffix}",
                    SourcePath: normalizedPath));
            }
        }
    }

    private static bool IsSharedWorldsInternalDirectoryName(string directoryName)
    {
        return directoryName.Contains(SharedWorldsBackupMarker, StringComparison.OrdinalIgnoreCase) ||
               directoryName.Contains(SharedWorldsStagingMarker, StringComparison.OrdinalIgnoreCase) ||
               directoryName.Contains(SharedWorldsRollbackMarker, StringComparison.OrdinalIgnoreCase);
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
            exception is IOException or UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }

        return (attributes & FileAttributes.ReparsePoint) == 0;
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
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
        catch (IOException)
        {
            return DateTime.MinValue;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private sealed record PalworldSaveRoot(string Path, string Kind);
}
