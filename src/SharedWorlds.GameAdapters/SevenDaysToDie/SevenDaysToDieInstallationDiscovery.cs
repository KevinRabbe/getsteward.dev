using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal static partial class SevenDaysToDieInstallationDiscovery
{
    internal const string ClientExecutablePathKey = "clientExecutablePath";
    internal const string DedicatedServerRootPathKey = "dedicatedServerRootPath";
    internal const string DedicatedServerExecutablePathKey = "dedicatedServerExecutablePath";
    internal const string DedicatedServerManifestPathKey = "dedicatedServerManifestPath";
    internal const string DedicatedServerInstallStateKey = "dedicatedServerInstallState";
    internal const string UserDataPathKey = "userDataPath";
    internal const string GameSteamAppIdKey = "gameSteamAppId";
    internal const string DedicatedServerSteamAppIdKey = "dedicatedServerSteamAppId";

    internal const string GameSteamAppId = "251570";
    internal const string DedicatedServerSteamAppId = "294420";

    public static IReadOnlyList<GameInstallation> Discover()
    {
        var userDataPath = ResolveDefaultUserDataPath();
        return DiscoverFromSteamLibraries(DiscoverSteamLibraries(), userDataPath);
    }

    internal static IReadOnlyList<GameInstallation> DiscoverFromSteamLibraries(
        IEnumerable<string> steamLibraries,
        string userDataPath)
    {
        ArgumentNullException.ThrowIfNull(steamLibraries);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataPath);

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var libraries = steamLibraries
            .Select(NormalizePathOrNull)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(comparer)
            .ToArray();
        var servers = DiscoverDedicatedServers(libraries, comparer);
        var installations = new List<GameInstallation>();
        var seenClientRoots = new HashSet<string>(comparer);

        foreach (var library in libraries)
        {
            var manifestPath = Path.Combine(library, "steamapps", $"appmanifest_{GameSteamAppId}.acf");
            var clientRoot = ResolveManifestInstallRoot(library, manifestPath)
                ?? NormalizePathOrNull(Path.Combine(library, "steamapps", "common", "7 Days To Die"));
            if (clientRoot is null || !seenClientRoots.Add(clientRoot))
            {
                continue;
            }

            var clientExecutable = Path.Combine(clientRoot, "7DaysToDie.exe");
            if (!File.Exists(clientExecutable))
            {
                continue;
            }

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ClientExecutablePathKey] = Path.GetFullPath(clientExecutable),
                [UserDataPathKey] = Path.GetFullPath(userDataPath),
                [GameSteamAppIdKey] = GameSteamAppId,
                [DedicatedServerSteamAppIdKey] = DedicatedServerSteamAppId
            };

            var server = servers.FirstOrDefault(candidate => comparer.Equals(candidate.LibraryPath, library))
                ?? servers.FirstOrDefault();
            if (server is not null)
            {
                metadata[DedicatedServerInstallStateKey] = "installed";
                metadata[DedicatedServerRootPathKey] = server.RootPath;
                metadata[DedicatedServerExecutablePathKey] = server.ExecutablePath;
                if (server.ManifestPath is not null)
                {
                    metadata[DedicatedServerManifestPathKey] = server.ManifestPath;
                }
            }
            else
            {
                metadata[DedicatedServerInstallStateKey] = "not-installed-in-discovered-steam-libraries";
            }

            installations.Add(new GameInstallation(
                Id: $"7-days-to-die:{clientRoot}",
                RootPath: clientRoot,
                Source: "steam",
                Metadata: metadata));
        }

        return installations;
    }

    private static IReadOnlyList<DedicatedServerInstallation> DiscoverDedicatedServers(
        IReadOnlyList<string> libraries,
        StringComparer comparer)
    {
        var servers = new List<DedicatedServerInstallation>();
        var seen = new HashSet<string>(comparer);

        foreach (var library in libraries)
        {
            var manifestPath = Path.Combine(
                library,
                "steamapps",
                $"appmanifest_{DedicatedServerSteamAppId}.acf");
            var serverRoot = ResolveManifestInstallRoot(library, manifestPath)
                ?? NormalizePathOrNull(Path.Combine(
                    library,
                    "steamapps",
                    "common",
                    "7 Days to Die Dedicated Server"));
            if (serverRoot is null || !seen.Add(serverRoot))
            {
                continue;
            }

            var executable = Path.Combine(serverRoot, "7DaysToDieServer.exe");
            if (!File.Exists(executable))
            {
                continue;
            }

            servers.Add(new DedicatedServerInstallation(
                LibraryPath: library,
                RootPath: serverRoot,
                ExecutablePath: Path.GetFullPath(executable),
                ManifestPath: File.Exists(manifestPath) ? Path.GetFullPath(manifestPath) : null));
        }

        return servers;
    }

    private static string? ResolveManifestInstallRoot(string library, string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var match = SteamInstallDirRegex().Match(File.ReadAllText(manifestPath));
            if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                return null;
            }

            var installDirectory = match.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
            return NormalizePathOrNull(Path.Combine(library, "steamapps", "common", installDirectory));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> DiscoverSteamLibraries()
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var roots = new HashSet<string>(comparer);

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in DiscoverWindowsSteamRoots())
            {
                roots.Add(root);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(Path.Combine(home, ".steam", "steam"));
            roots.Add(Path.Combine(home, ".local", "share", "Steam"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(Path.Combine(home, "Library", "Application Support", "Steam"));
        }

        var libraries = new HashSet<string>(roots, comparer);
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
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        libraries.Add(value);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Discovery is best-effort; inaccessible Steam metadata is skipped.
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
            // Registry access is optional. Conventional Steam paths still work.
        }

        return roots;
    }

    private static string ResolveDefaultUserDataPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
            {
                return Path.Combine(appData, "7DaysToDie");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                return Path.Combine(home, ".local", "share", "7DaysToDie");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                return Path.Combine(home, "Library", "Application Support", "7DaysToDie");
            }
        }

        return Path.Combine(Path.GetTempPath(), "7DaysToDie");
    }

    private static string? NormalizePathOrNull(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return null;
        }
    }

    [GeneratedRegex("\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamLibraryPathRegex();

    [GeneratedRegex("\\\"installdir\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamInstallDirRegex();

    private sealed record DedicatedServerInstallation(
        string LibraryPath,
        string RootPath,
        string ExecutablePath,
        string? ManifestPath);
}
