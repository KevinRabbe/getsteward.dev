using System.Globalization;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.AbioticFactor;

internal static class AbioticFactorWorldDiscovery
{
    private const int MaximumDiscoveryEntries = 50_000;

    public static IReadOnlyList<DetectedWorld> Discover(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                AbioticFactorInstallationDiscovery.SaveGamesRootPathKey,
                out var saveGamesRoot) ||
            string.IsNullOrWhiteSpace(saveGamesRoot))
        {
            return [];
        }

        return DiscoverFromSaveGamesRoot(saveGamesRoot);
    }

    internal static IReadOnlyList<DetectedWorld> DiscoverFromSaveGamesRoot(string saveGamesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveGamesRoot);
        var root = Path.GetFullPath(saveGamesRoot);
        if (!IsRegularDirectory(root))
        {
            return [];
        }

        var worlds = new List<DetectedWorld>();
        try
        {
            foreach (var profile in Directory.EnumerateDirectories(
                         root,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (!IsCanonicalSteamId64Directory(profile))
                {
                    continue;
                }

                var worldsRoot = Path.Combine(profile, "Worlds");
                if (!IsRegularDirectory(worldsRoot))
                {
                    continue;
                }

                foreach (var candidate in Directory.EnumerateDirectories(
                             worldsRoot,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    if (!TryInspectWorldTree(candidate))
                    {
                        continue;
                    }

                    var profileId = Path.GetFileName(profile);
                    var worldName = Path.GetFileName(candidate);
                    worlds.Add(new DetectedWorld(
                        $"local:{profileId}:{worldName}",
                        worldName,
                        Path.GetFullPath(candidate)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return worlds
            .OrderByDescending(world => GetLastWriteTimeUtcSafe(world.SourcePath))
            .ThenBy(world => world.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool IsRegularDirectory(string path)
        => TryGetRegularAttributes(path, expectDirectory: true);

    internal static bool IsRegularFile(string path)
        => TryGetRegularAttributes(path, expectDirectory: false);

    internal static bool TryInspectWorldTree(string worldRoot)
    {
        if (!IsRegularDirectory(worldRoot))
        {
            return false;
        }

        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(worldRoot));
        var entries = 0;
        var foundWorldData = false;

        try
        {
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!IsRegularDirectory(current))
                {
                    return false;
                }

                foreach (var directory in Directory.EnumerateDirectories(
                             current,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    entries++;
                    if (entries > MaximumDiscoveryEntries || !IsRegularDirectory(directory))
                    {
                        return false;
                    }

                    pending.Push(directory);
                }

                foreach (var file in Directory.EnumerateFiles(
                             current,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    entries++;
                    if (entries > MaximumDiscoveryEntries || !IsRegularFile(file))
                    {
                        return false;
                    }

                    if (string.Equals(
                            Path.GetExtension(file),
                            ".sav",
                            StringComparison.OrdinalIgnoreCase) &&
                        new FileInfo(file).Length > 0)
                    {
                        foundWorldData = true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return foundWorldData;
    }

    private static bool IsCanonicalSteamId64Directory(string path)
    {
        if (!IsRegularDirectory(path))
        {
            return false;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return ulong.TryParse(
                   name,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out var value) &&
               value != 0 &&
               string.Equals(
                   value.ToString(CultureInfo.InvariantCulture),
                   name,
                   StringComparison.Ordinal);
    }

    private static bool TryGetRegularAttributes(string path, bool expectDirectory)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) == 0 &&
                   ((attributes & FileAttributes.Directory) != 0) == expectDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static DateTime GetLastWriteTimeUtcSafe(string path)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
