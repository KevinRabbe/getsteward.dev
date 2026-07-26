using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Icarus;

internal static partial class IcarusInstallationDiscovery
{
    internal const string ClientExecutablePathKey = "clientExecutablePath";
    internal const string SteamManifestPathKey = "steamManifestPath";
    internal const string PlayerDataRootPathKey = "playerDataRootPath";
    internal const string ModsRootPathKey = "modsRootPath";
    internal const string GameSteamAppIdKey = "gameSteamAppId";
    internal const string GameSteamAppId = "1149460";

    public static IReadOnlyList<GameInstallation> Discover()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            return [];
        }

        var playerDataRoot = Path.Combine(localData, "Icarus", "Saved", "PlayerData");
        return DiscoverFromSteamLibraries(DiscoverSteamLibraries(), playerDataRoot);
    }

    internal static IReadOnlyList<GameInstallation> DiscoverFromSteamLibraries(
        IEnumerable<string> steamLibraries,
        string playerDataRoot)
    {
        ArgumentNullException.ThrowIfNull(steamLibraries);
        ArgumentException.ThrowIfNullOrWhiteSpace(playerDataRoot);
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var installations = new List<GameInstallation>();
        var seen = new HashSet<string>(comparer);

        foreach (var raw in steamLibraries)
        {
            var library = NormalizePathOrNull(raw);
            if (library is null)
            {
                continue;
            }

            var root = NormalizePathOrNull(Path.Combine(
                library,
                "steamapps",
                "common",
                "Icarus"));
            if (root is null || !seen.Add(root))
            {
                continue;
            }

            var executable = Path.Combine(root, "Icarus.exe");
            if (!File.Exists(executable))
            {
                continue;
            }

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ClientExecutablePathKey] = Path.GetFullPath(executable),
                [PlayerDataRootPathKey] = Path.GetFullPath(playerDataRoot),
                [ModsRootPathKey] = Path.GetFullPath(Path.Combine(root, "Icarus", "Content", "Paks", "mods")),
                [GameSteamAppIdKey] = GameSteamAppId
            };
            var manifest = Path.Combine(
                library,
                "steamapps",
                $"appmanifest_{GameSteamAppId}.acf");
            if (File.Exists(manifest))
            {
                metadata[SteamManifestPathKey] = Path.GetFullPath(manifest);
            }

            installations.Add(new GameInstallation(
                $"icarus:{root}",
                root,
                "steam",
                metadata));
        }

        return installations;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> DiscoverSteamLibraries()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            roots.Add(Path.Combine(programFilesX86, "Steam"));
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steamPath &&
                !string.IsNullOrWhiteSpace(steamPath))
            {
                roots.Add(steamPath);
            }
        }
        catch
        {
            // Registry discovery is optional.
        }

        var libraries = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        foreach (var steamRoot in roots)
        {
            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
            {
                continue;
            }

            try
            {
                foreach (Match match in SteamLibraryPathRegex().Matches(File.ReadAllText(vdf)))
                {
                    var value = match.Groups[1].Value.Replace(
                        "\\\\",
                        "\\",
                        StringComparison.Ordinal);
                    var normalized = NormalizePathOrNull(value);
                    if (normalized is not null)
                    {
                        libraries.Add(normalized);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Steam discovery is best-effort.
            }
        }

        return libraries;
    }

    private static string? NormalizePathOrNull(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    [GeneratedRegex("\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamLibraryPathRegex();
}
