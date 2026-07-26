using System.Globalization;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Raft;

internal static class RaftWorldDiscovery
{
    public static IReadOnlyList<DetectedWorld> Discover(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(RaftInstallationDiscovery.UserRootPathKey, out var userRoot) ||
            string.IsNullOrWhiteSpace(userRoot)) return [];
        return DiscoverFromUserRoot(userRoot);
    }

    internal static IReadOnlyList<DetectedWorld> DiscoverFromUserRoot(string userRoot)
    {
        var fullRoot = Path.GetFullPath(userRoot);
        if (!IsRegularDirectory(fullRoot)) return [];
        var worlds = new List<DetectedWorld>();
        try
        {
            foreach (var profile in Directory.EnumerateDirectories(fullRoot, "User_*", SearchOption.TopDirectoryOnly))
            {
                if (!IsRegularDirectory(profile)) continue;
                var profileName = Path.GetFileName(profile);
                if (!TryGetSteamId(profileName, out _)) continue;
                var worldRoot = Path.Combine(profile, "World");
                if (!IsRegularDirectory(worldRoot)) continue;
                foreach (var worldDirectory in Directory.EnumerateDirectories(worldRoot, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsRegularDirectory(worldDirectory)) continue;
                    var worldName = Path.GetFileName(worldDirectory);
                    if (string.IsNullOrWhiteSpace(worldName)) continue;
                    var current = Path.Combine(worldDirectory, worldName + ".rgd");
                    if (!IsRegularNonEmptyFile(current)) continue;
                    worlds.Add(new DetectedWorld(
                        $"local:{profileName}:{worldName}",
                        worldName,
                        Path.GetFullPath(current)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        return worlds
            .OrderByDescending(world => GetLastWriteTimeUtcSafe(world.SourcePath))
            .ThenBy(world => world.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool TryGetSteamId(string profileName, out ulong steamId)
    {
        steamId = 0;
        const string prefix = "User_";
        if (!profileName.StartsWith(prefix, StringComparison.Ordinal) || profileName.Length == prefix.Length) return false;
        var value = profileName[prefix.Length..];
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out steamId)) return false;
        return string.Equals(steamId.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal);
    }

    internal static bool IsRegularNonEmptyFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 && new FileInfo(path).Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static bool IsRegularDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static DateTime GetLastWriteTimeUtcSafe(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }
}
