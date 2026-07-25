using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Terraria;

internal static class TerrariaWorldDiscovery
{
    public static IReadOnlyList<DetectedWorld> Discover(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(TerrariaInstallationDiscovery.UserDataPathKey, out var worldRoot) ||
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
        if (!TryRequireRegularDirectory(fullRoot))
        {
            return [];
        }

        try
        {
            return Directory
                .EnumerateFiles(fullRoot, "*.wld", SearchOption.TopDirectoryOnly)
                .Where(TryRequireRegularFile)
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

    private static bool TryRequireRegularDirectory(string path)
        => TryRequireRegularPath(path, expectDirectory: true);

    private static bool TryRequireRegularFile(string path)
        => TryRequireRegularPath(path, expectDirectory: false);

    private static bool TryRequireRegularPath(string path, bool expectDirectory)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) == 0 &&
                   ((attributes & FileAttributes.Directory) != 0) == expectDirectory;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
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
