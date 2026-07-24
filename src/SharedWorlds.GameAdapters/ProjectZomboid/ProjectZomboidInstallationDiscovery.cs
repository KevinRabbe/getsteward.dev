using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal static partial class ProjectZomboidInstallationDiscovery
{
    internal const string ClientExecutablePathKey = "clientExecutablePath";
    internal const string DedicatedServerRootPathKey = "dedicatedServerRootPath";
    internal const string DedicatedServerLaunchPathKey = "dedicatedServerLaunchPath";
    internal const string DedicatedServerManifestPathKey = "dedicatedServerManifestPath";
    internal const string DedicatedServerInstallStateKey = "dedicatedServerInstallState";
    internal const string UserDataPathKey = "userDataPath";
    internal const string GameSteamAppIdKey = "gameSteamAppId";
    internal const string DedicatedServerSteamAppIdKey = "dedicatedServerSteamAppId";

    internal const string GameSteamAppId = "108600";
    internal const string DedicatedServerSteamAppId = "380870";

    public static IReadOnlyList<GameInstallation> Discover()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
        {
            return [];
        }

        return DiscoverFromSteamLibraries(
            DiscoverWindowsSteamLibraries(),
            Path.Combine(userProfile, "Zomboid"));
    }

    internal static IReadOnlyList<GameInstallation> DiscoverFromSteamLibraries(
        IEnumerable<string> steamLibraries,
        string userDataPath)
    {
        ArgumentNullException.ThrowIfNull(steamLibraries);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataPath);

        var comparer = StringComparer.OrdinalIgnoreCase;
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
                ?? NormalizePathOrNull(Path.Combine(library, "steamapps", "common", "ProjectZomboid"));
            if (clientRoot is null || !seenClientRoots.Add(clientRoot))
            {
                continue;
            }

            var clientExecutable = Path.Combine(clientRoot, "ProjectZomboid64.exe");
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
            if (server is null)
            {
                metadata[DedicatedServerInstallStateKey] = "not-installed-in-discovered-steam-libraries";
            }
            else
            {
                metadata[DedicatedServerInstallStateKey] = "installed";
                metadata[DedicatedServerRootPathKey] = server.RootPath;
                metadata[DedicatedServerLaunchPathKey] = server.LaunchPath;
                if (server.ManifestPath is not null)
                {
                    metadata[DedicatedServerManifestPathKey] = server.ManifestPath;
                }
            }

            installations.Add(new GameInstallation(
                Id: $"project-zomboid:{clientRoot}",
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
        var result = new List<DedicatedServerInstallation>();
        var seen = new HashSet<string>(comparer);
        foreach (var library in libraries)
        {
            var manifestPath = Path.Combine(
                library,
                "steamapps",
                $"appmanifest_{DedicatedServerSteamAppId}.acf");
            var root = ResolveManifestInstallRoot(library, manifestPath)
                ?? NormalizePathOrNull(Path.Combine(
                    library,
                    "steamapps",
                    "common",
                    "Project Zomboid Dedicated Server"));
            if (root is null || !seen.Add(root))
            {
                continue;
            }

            var launchPath = Path.Combine(root, "StartServer64.bat");
            if (!File.Exists(launchPath))
            {
                continue;
            }

            result.Add(new DedicatedServerInstallation(
                library,
                root,
                Path.GetFullPath(launchPath),
                File.Exists(manifestPath) ? Path.GetFullPath(manifestPath) : null));
        }

        return result;
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

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> DiscoverWindowsSteamLibraries()
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

        var libraries = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var libraryFoldersPath = Path.Combine(root, "steamapps", "libraryfolders.vdf");
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
                // Best-effort discovery; other Steam roots remain usable.
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
        string LaunchPath,
        string? ManifestPath);
}
