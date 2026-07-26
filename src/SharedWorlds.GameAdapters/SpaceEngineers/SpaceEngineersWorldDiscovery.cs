using System.Globalization;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.SpaceEngineers;

internal static class SpaceEngineersWorldDiscovery
{
    internal const string SandboxFileName = "Sandbox.sbc";
    internal const string SandboxConfigFileName = "Sandbox_config.sbc";
    internal const string SectorFileName = "SANDBOX_0_0_0_.sbs";
    internal const string BackupDirectoryName = "Backup";
    private const string OfflineProfileId = "1234567891011";
    private const int MaximumDiscoveryEntries = 100_000;

    public static IReadOnlyList<DetectedWorld> Discover(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                SpaceEngineersInstallationDiscovery.SaveProfilesRootPathKey,
                out var saveProfilesRoot) ||
            string.IsNullOrWhiteSpace(saveProfilesRoot))
        {
            return [];
        }

        return DiscoverFromSaveProfilesRoot(saveProfilesRoot);
    }

    internal static IReadOnlyList<DetectedWorld> DiscoverFromSaveProfilesRoot(string saveProfilesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(saveProfilesRoot);
        var root = Path.GetFullPath(saveProfilesRoot);
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
                if (!IsCanonicalSteamProfile(profile))
                {
                    continue;
                }

                var profileId = Path.GetFileName(Path.TrimEndingDirectorySeparator(profile));
                foreach (var candidate in Directory.EnumerateDirectories(
                             profile,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    if (!TryInspectCurrentWorldTree(candidate))
                    {
                        continue;
                    }

                    var worldName = Path.GetFileName(Path.TrimEndingDirectorySeparator(candidate));
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

    internal static bool TryInspectCurrentWorldTree(string worldRoot)
    {
        if (!IsRegularDirectory(worldRoot) ||
            !IsRegularNonEmptyFile(Path.Combine(worldRoot, SandboxFileName)) ||
            !IsRegularNonEmptyFile(Path.Combine(worldRoot, SandboxConfigFileName)) ||
            !IsRegularNonEmptyFile(Path.Combine(worldRoot, SectorFileName)))
        {
            return false;
        }

        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(worldRoot));
        var entries = 0;

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
                    if (PathsEqual(current, worldRoot) &&
                        string.Equals(
                            Path.GetFileName(directory),
                            BackupDirectoryName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

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
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return true;
    }

    internal static bool IsRegularDirectory(string path)
        => TryGetRegularAttributes(path, expectDirectory: true);

    internal static bool IsRegularFile(string path)
        => TryGetRegularAttributes(path, expectDirectory: false);

    internal static bool IsRegularNonEmptyFile(string path)
    {
        if (!IsRegularFile(path))
        {
            return false;
        }

        try
        {
            return new FileInfo(path).Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsCanonicalSteamProfile(string path)
    {
        if (!IsRegularDirectory(path))
        {
            return false;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return !string.Equals(name, OfflineProfileId, StringComparison.Ordinal) &&
               ulong.TryParse(
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

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
