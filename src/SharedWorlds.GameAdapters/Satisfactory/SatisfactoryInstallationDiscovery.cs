using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Satisfactory;

internal static partial class SatisfactoryInstallationDiscovery
{
    internal const string ClientExecutablePathKey = "clientExecutablePath";
    internal const string SteamManifestPathKey = "steamManifestPath";
    internal const string SaveGamesRootPathKey = "saveGamesRootPath";
    internal const string ModsRootPathKey = "modsRootPath";
    internal const string WorkshopContentRootPathKey = "workshopContentRootPath";
    internal const string GameSteamAppIdKey = "gameSteamAppId";
    internal const string GameSteamAppId = "526870";

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

        var saveGamesRoot = Path.Combine(
            localData,
            "FactoryGame",
            "Saved",
            "SaveGames");
        return DiscoverFromSteamLibraries(DiscoverSteamLibraries(), saveGamesRoot);
    }

    internal static IReadOnlyList<GameInstallation> DiscoverFromSteamLibraries(
        IEnumerable<string> steamLibraries,
        string saveGamesRoot)
    {
        ArgumentNullException.ThrowIfNull(steamLibraries);
        ArgumentException.ThrowIfNullOrWhiteSpace(saveGamesRoot);

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var installations = new List<GameInstallation>();
        var seenRoots = new HashSet<string>(comparer);

        foreach (var rawLibrary in steamLibraries)
        {
            var library = NormalizePathOrNull(rawLibrary);
            if (library is null)
            {
                continue;
            }

            var root = NormalizePathOrNull(Path.Combine(
                library,
                "steamapps",
                "common",
                "Satisfactory"));
            if (root is null || !seenRoots.Add(root))
            {
                continue;
            }

            var executable = Path.Combine(root, "FactoryGameSteam.exe");
            if (!File.Exists(executable))
            {
                continue;
            }

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ClientExecutablePathKey] = Path.GetFullPath(executable),
                [SaveGamesRootPathKey] = Path.GetFullPath(saveGamesRoot),
                [ModsRootPathKey] = Path.GetFullPath(Path.Combine(
                    root,
                    "FactoryGame",
                    "Mods")),
                [WorkshopContentRootPathKey] = Path.GetFullPath(Path.Combine(
                    library,
                    "steamapps",
                    "workshop",
                    "content",
                    GameSteamAppId)),
                [GameSteamAppIdKey] = GameSteamAppId
            };

            var manifestPath = Path.Combine(
                library,
                "steamapps",
                $"appmanifest_{GameSteamAppId}.acf");
            if (File.Exists(manifestPath))
            {
                metadata[SteamManifestPathKey] = Path.GetFullPath(manifestPath);
            }

            installations.Add(new GameInstallation(
                Id: $"satisfactory:{root}",
                RootPath: root,
                Source: "steam",
                Metadata: metadata));
        }

        return installations;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> DiscoverSteamLibraries()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in DiscoverWindowsSteamRoots())
        {
            var normalized = NormalizePathOrNull(root);
            if (normalized is not null)
            {
                roots.Add(normalized);
            }
        }

        var libraries = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        foreach (var steamRoot in roots)
        {
            var libraryFoldersPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFoldersPath))
            {
                continue;
            }

            try
            {
                var text = File.ReadAllText(libraryFoldersPath);
                foreach (Match match in SteamLibraryPathRegex().Matches(text))
                {
                    var value = match.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
                    var normalized = NormalizePathOrNull(value);
                    if (normalized is not null)
                    {
                        libraries.Add(normalized);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Steam discovery is best-effort.
            }
        }

        return libraries;
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> DiscoverWindowsSteamRoots()
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
            if (key?.GetValue("SteamPath") is string steamPath && !string.IsNullOrWhiteSpace(steamPath))
            {
                roots.Add(steamPath);
            }
        }
        catch
        {
            // Registry discovery is optional.
        }

        return roots;
    }

    private static string? NormalizePathOrNull(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    [GeneratedRegex("\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamLibraryPathRegex();
}
