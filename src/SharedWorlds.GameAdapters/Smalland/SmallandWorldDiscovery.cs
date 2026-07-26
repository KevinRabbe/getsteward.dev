using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Smalland;

internal static class SmallandWorldDiscovery
{
    public static IReadOnlyList<DetectedWorld> Discover(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                SmallandInstallationDiscovery.SaveGamesRootPathKey,
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
        var worldsRoot = Path.GetFullPath(Path.Combine(saveGamesRoot, "Worlds"));
        if (!IsRegularDirectory(worldsRoot))
        {
            return [];
        }

        var worlds = new List<DetectedWorld>();
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(
                         worldsRoot,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (!string.Equals(
                        Path.GetExtension(candidate),
                        ".wld",
                        StringComparison.OrdinalIgnoreCase) ||
                    !IsRegularNonEmptyFile(candidate))
                {
                    continue;
                }

                var name = Path.GetFileNameWithoutExtension(candidate);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                worlds.Add(new DetectedWorld(
                    $"local:{name}",
                    name,
                    Path.GetFullPath(candidate)));
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

    internal static bool IsRegularNonEmptyFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 &&
                   new FileInfo(path).Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static DateTime GetLastWriteTimeUtcSafe(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }
}
