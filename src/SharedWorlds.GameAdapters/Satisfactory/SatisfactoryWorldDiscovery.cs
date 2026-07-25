using System.Globalization;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Satisfactory;

internal static class SatisfactoryWorldDiscovery
{
    public static IReadOnlyList<DetectedWorld> Discover(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                SatisfactoryInstallationDiscovery.SaveGamesRootPathKey,
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
        var fullRoot = Path.GetFullPath(saveGamesRoot);
        if (!IsRegularDirectory(fullRoot))
        {
            return [];
        }

        var worlds = new List<DetectedWorld>();
        try
        {
            foreach (var profilePath in Directory.EnumerateDirectories(
                         fullRoot,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (!IsRegularDirectory(profilePath))
                {
                    continue;
                }

                var profileName = Path.GetFileName(profilePath);
                if (!IsCanonicalSteamId(profileName))
                {
                    continue;
                }

                foreach (var savePath in Directory.EnumerateFiles(
                             profilePath,
                             "*.sav",
                             SearchOption.TopDirectoryOnly))
                {
                    if (!IsRegularNonEmptyFile(savePath))
                    {
                        continue;
                    }

                    var nativeName = Path.GetFileNameWithoutExtension(savePath);
                    worlds.Add(new DetectedWorld(
                        Id: $"local:{profileName}:{nativeName}",
                        DisplayName: nativeName,
                        SourcePath: Path.GetFullPath(savePath)));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return worlds
            .OrderByDescending(world => GetLastWriteTimeUtcSafe(world.SourcePath))
            .ThenBy(world => world.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool IsCanonicalSteamId(string value)
    {
        if (!ulong.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }

        return string.Equals(
            parsed.ToString(CultureInfo.InvariantCulture),
            value,
            StringComparison.Ordinal);
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
