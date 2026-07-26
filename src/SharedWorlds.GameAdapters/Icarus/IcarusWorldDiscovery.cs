using System.Globalization;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Icarus;

internal static class IcarusWorldDiscovery
{
    public static IReadOnlyList<DetectedWorld> Discover(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                IcarusInstallationDiscovery.PlayerDataRootPathKey,
                out var playerDataRoot) ||
            string.IsNullOrWhiteSpace(playerDataRoot))
        {
            return [];
        }

        return DiscoverFromPlayerDataRoot(playerDataRoot);
    }

    internal static IReadOnlyList<DetectedWorld> DiscoverFromPlayerDataRoot(string playerDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerDataRoot);
        var fullRoot = Path.GetFullPath(playerDataRoot);
        if (!IsRegularDirectory(fullRoot))
        {
            return [];
        }

        var worlds = new List<DetectedWorld>();
        try
        {
            foreach (var profile in Directory.EnumerateDirectories(
                         fullRoot,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (!IsRegularDirectory(profile))
                {
                    continue;
                }

                var profileName = Path.GetFileName(profile);
                if (!TryGetSteamId(profileName, out _))
                {
                    continue;
                }

                var prospectsRoot = Path.Combine(profile, "Prospects");
                if (!IsRegularDirectory(prospectsRoot))
                {
                    continue;
                }

                foreach (var candidate in Directory.EnumerateFiles(
                             prospectsRoot,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    if (!IsCurrentProspectPath(candidate) || !IsRegularNonEmptyFile(candidate))
                    {
                        continue;
                    }

                    var name = Path.GetFileNameWithoutExtension(candidate);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    worlds.Add(new DetectedWorld(
                        $"local:{profileName}:{name}",
                        name,
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

    internal static bool TryGetSteamId(string profileName, out ulong steamId)
    {
        steamId = 0;
        if (!ulong.TryParse(
                profileName,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out steamId))
        {
            return false;
        }

        return string.Equals(
            steamId.ToString(CultureInfo.InvariantCulture),
            profileName,
            StringComparison.Ordinal);
    }

    internal static bool IsCurrentProspectPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return string.Equals(
                   Path.GetExtension(fileName),
                   ".json",
                   StringComparison.OrdinalIgnoreCase) &&
               !fileName.Contains(".backup", StringComparison.OrdinalIgnoreCase) &&
               !fileName.Contains(".bak", StringComparison.OrdinalIgnoreCase);
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
