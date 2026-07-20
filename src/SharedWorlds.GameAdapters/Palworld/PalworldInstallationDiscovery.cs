using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Palworld;

internal static partial class PalworldInstallationDiscovery
{
    internal const string ClientExecutablePathKey = "clientExecutablePath";
    internal const string DedicatedServerRootPathKey = "dedicatedServerRootPath";
    internal const string DedicatedServerExecutablePathKey = "dedicatedServerExecutablePath";
    internal const string DedicatedServerManifestPathKey = "dedicatedServerManifestPath";
    internal const string DedicatedServerInstallStateKey = "dedicatedServerInstallState";
    internal const string GameSteamAppIdKey = "gameSteamAppId";
    internal const string DedicatedServerSteamAppIdKey = "dedicatedServerSteamAppId";

    internal const string GameSteamAppId = "1623730";
    internal const string DedicatedServerSteamAppId = "2394010";

    public static IReadOnlyList<GameInstallation> Discover()
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var installations = new List<GameInstallation>();
        var seen = new HashSet<string>(comparer);
        var steamLibraries = DiscoverSteamLibraries()
            .Select(NormalizePathOrNull)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(comparer)
            .ToArray();
        var serverManifests = DiscoverDedicatedServerManifests(steamLibraries, comparer);
        var dedicatedServers = DiscoverDedicatedServers(steamLibraries, serverManifests, comparer);

        foreach (var steamLibrary in steamLibraries)
        {
            var gameRoot = Path.Combine(steamLibrary, "steamapps", "common", "Palworld");
            var clientExecutable = FindClientExecutable(gameRoot);
            if (clientExecutable is null)
            {
                continue;
            }

            var normalizedGameRoot = NormalizePathOrNull(gameRoot);
            if (normalizedGameRoot is null || !seen.Add(normalizedGameRoot))
            {
                continue;
            }

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ClientExecutablePathKey] = clientExecutable,
                [GameSteamAppIdKey] = GameSteamAppId,
                [DedicatedServerSteamAppIdKey] = DedicatedServerSteamAppId
            };

            // The Steam client and the dedicated-server tool may be installed in different
            // Steam libraries. Prefer a server beside this client, but fall back to any
            // discovered PalServer installation rather than coupling discovery to one library.
            var dedicatedServer = dedicatedServers.FirstOrDefault(server =>
                    comparer.Equals(server.LibraryPath, steamLibrary))
                ?? dedicatedServers.FirstOrDefault();

            if (dedicatedServer is not null)
            {
                metadata[DedicatedServerInstallStateKey] = "installed";
                metadata[DedicatedServerRootPathKey] = dedicatedServer.RootPath;
                metadata[DedicatedServerExecutablePathKey] = dedicatedServer.ExecutablePath;
                if (dedicatedServer.ManifestPath is not null)
                {
                    metadata[DedicatedServerManifestPathKey] = dedicatedServer.ManifestPath;
                }
            }
            else
            {
                var manifest = serverManifests.FirstOrDefault(candidate =>
                        comparer.Equals(candidate.LibraryPath, steamLibrary))
                    ?? serverManifests.FirstOrDefault();

                if (manifest is not null)
                {
                    metadata[DedicatedServerInstallStateKey] = "manifest-found-executable-missing";
                    metadata[DedicatedServerManifestPathKey] = manifest.ManifestPath;
                    metadata[DedicatedServerRootPathKey] = manifest.RootPath;
                }
                else
                {
                    metadata[DedicatedServerInstallStateKey] = "not-installed-in-discovered-steam-libraries";
                }
            }

            installations.Add(new GameInstallation(
                Id: $"palworld:{normalizedGameRoot}",
                RootPath: normalizedGameRoot,
                Source: "steam",
                Metadata: metadata));
        }

        return installations;
    }

    private static IReadOnlyList<DedicatedServerManifest> DiscoverDedicatedServerManifests(
        IEnumerable<string> steamLibraries,
        StringComparer comparer)
    {
        var manifests = new List<DedicatedServerManifest>();
        var seenPaths = new HashSet<string>(comparer);

        foreach (var steamLibrary in steamLibraries)
        {
            var manifestPath = Path.Combine(
                steamLibrary,
                "steamapps",
                $"appmanifest_{DedicatedServerSteamAppId}.acf");
            if (!File.Exists(manifestPath) || !seenPaths.Add(manifestPath))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(manifestPath);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            var match = SteamInstallDirRegex().Match(text);
            if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                continue;
            }

            var installDirectoryName = match.Groups[1].Value
                .Replace("\\\\", "\\", StringComparison.Ordinal);
            var rootPath = NormalizePathOrNull(Path.Combine(
                steamLibrary,
                "steamapps",
                "common",
                installDirectoryName));
            if (rootPath is null)
            {
                continue;
            }

            manifests.Add(new DedicatedServerManifest(
                LibraryPath: steamLibrary,
                ManifestPath: manifestPath,
                RootPath: rootPath));
        }

        return manifests;
    }

    private static IReadOnlyList<DedicatedServerInstallation> DiscoverDedicatedServers(
        IReadOnlyList<string> steamLibraries,
        IReadOnlyList<DedicatedServerManifest> manifests,
        StringComparer comparer)
    {
        var servers = new List<DedicatedServerInstallation>();
        var seenRoots = new HashSet<string>(comparer);

        foreach (var manifest in manifests)
        {
            AddServerIfPresent(
                manifest.LibraryPath,
                manifest.RootPath,
                manifest.ManifestPath,
                servers,
                seenRoots);
        }

        // Keep the documented conventional path as a fallback for SteamCMD/manual layouts
        // where no Steam client app manifest is present in the discovered library.
        foreach (var steamLibrary in steamLibraries)
        {
            var serverRoot = Path.Combine(steamLibrary, "steamapps", "common", "PalServer");
            AddServerIfPresent(
                steamLibrary,
                serverRoot,
                manifestPath: null,
                servers,
                seenRoots);
        }

        return servers;
    }

    private static void AddServerIfPresent(
        string libraryPath,
        string rootPath,
        string? manifestPath,
        ICollection<DedicatedServerInstallation> servers,
        ISet<string> seenRoots)
    {
        var normalizedServerRoot = NormalizePathOrNull(rootPath);
        if (normalizedServerRoot is null || !seenRoots.Add(normalizedServerRoot))
        {
            return;
        }

        var serverExecutable = FindDedicatedServerExecutable(normalizedServerRoot);
        if (serverExecutable is null)
        {
            return;
        }

        servers.Add(new DedicatedServerInstallation(
            LibraryPath: libraryPath,
            RootPath: normalizedServerRoot,
            ExecutablePath: serverExecutable,
            ManifestPath: manifestPath));
    }

    private static IEnumerable<string> DiscoverSteamLibraries()
    {
        var roots = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

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

        var libraries = new HashSet<string>(roots, roots.Comparer);
        foreach (var steamRoot in roots)
        {
            var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(libraryFile))
            {
                continue;
            }

            try
            {
                var text = File.ReadAllText(libraryFile);
                foreach (Match match in SteamLibraryPathRegex().Matches(text))
                {
                    var value = match.Groups[1].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        libraries.Add(value);
                    }
                }
            }
            catch (IOException)
            {
                // Discovery is best-effort; temporarily unavailable Steam metadata is skipped.
            }
            catch (UnauthorizedAccessException)
            {
                // Discovery is best-effort; inaccessible libraries are skipped.
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

    private static string? FindClientExecutable(string root)
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(root, "Palworld.exe") }
            : new[] { Path.Combine(root, "Palworld.exe") };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindDedicatedServerExecutable(string root)
    {
        string[] candidates;
        if (OperatingSystem.IsWindows())
        {
            candidates = [Path.Combine(root, "PalServer.exe")];
        }
        else if (OperatingSystem.IsLinux())
        {
            candidates = [Path.Combine(root, "PalServer.sh")];
        }
        else
        {
            return null;
        }

        return candidates.FirstOrDefault(File.Exists);
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

    private sealed record DedicatedServerManifest(
        string LibraryPath,
        string ManifestPath,
        string RootPath);

    private sealed record DedicatedServerInstallation(
        string LibraryPath,
        string RootPath,
        string ExecutablePath,
        string? ManifestPath);

    [GeneratedRegex("\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamLibraryPathRegex();

    [GeneratedRegex("\\\"installdir\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamInstallDirRegex();
}
