using System.Globalization;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.AbioticFactor;

internal static class AbioticFactorWorldDiscovery
{
    internal const string MetadataFileName = "WorldSave_MetaData.sav";

    public static IReadOnlyList<DetectedWorld> Discover(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                AbioticFactorInstallationDiscovery.SaveGamesRootPathKey,
                out var saveRoot) ||
            string.IsNullOrWhiteSpace(saveRoot))
        {
            return [];
        }

        return DiscoverFromSaveRoot(saveRoot);
    }

    internal static IReadOnlyList<DetectedWorld> DiscoverFromSaveRoot(string saveRoot)
    {
        var fullRoot = Path.GetFullPath(saveRoot);
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

                var worldRoot = Path.Combine(profile, "Worlds");
                if (!IsRegularDirectory(worldRoot))
                {
                    continue;
                }

                foreach (var worldDirectory in Directory.EnumerateDirectories(
                             worldRoot,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    if (!IsRegularDirectory(worldDirectory))
                    {
                        continue;
                    }

                    var worldName = Path.GetFileName(worldDirectory);
                    if (string.IsNullOrWhiteSpace(worldName))
                    {
                        continue;
                    }

                    var metadata = Path.Combine(worldDirectory, MetadataFileName);
                    if (!IsRegularNonEmptyFile(metadata))
                    {
                        continue;
                    }

                    worlds.Add(new DetectedWorld(
                        $"local:{profileName}:{worldName}",
                        worldName,
                        Path.GetFullPath(worldDirectory)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return worlds
            .OrderByDescending(world => GetLastWriteTimeUtcSafe(
                Path.Combine(world.SourcePath, MetadataFileName)))
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
