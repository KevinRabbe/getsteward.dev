using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Astroneer;

internal static class AstroneerWorldDiscovery
{
    public static IReadOnlyList<DetectedWorld> Discover(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                AstroneerInstallationDiscovery.WorldRootPathKey,
                out var worldRoot) ||
            string.IsNullOrWhiteSpace(worldRoot))
        {
            return [];
        }

        return DiscoverFromWorldRoot(worldRoot);
    }

    internal static IReadOnlyList<DetectedWorld> DiscoverFromWorldRoot(string worldRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldRoot);
        var fullRoot = Path.GetFullPath(worldRoot);
        if (!IsRegularDirectory(fullRoot))
        {
            return [];
        }

        try
        {
            return Directory
                .EnumerateFiles(fullRoot, "*.savegame", SearchOption.TopDirectoryOnly)
                .Where(IsRegularNonEmptyFile)
                .Select(path => new DetectedWorld(
                    Id: $"local:{Path.GetFileNameWithoutExtension(path)}",
                    DisplayName: Path.GetFileNameWithoutExtension(path),
                    SourcePath: Path.GetFullPath(path)))
                .OrderByDescending(world => GetLastWriteTimeUtcSafe(world.SourcePath))
                .ThenBy(world => world.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
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

    private static DateTime GetLastWriteTimeUtcSafe(string path)
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
