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
        var dedicatedServers = DiscoverDedicatedServers(steamLibraries, comparer);

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
                metadata[DedicatedServerRootPathKey] = dedicatedServer.RootPath;
                metadata[DedicatedServerExecutablePathKey] = dedicatedServer.ExecutablePath;
            }

            installations.Add(new GameInstallation(
                Id: $"palworld:{normalizedGameRoot}",
                RootPath: normalizedGameRoot,
                Source: "steam",
                Metadata: metadata));
        }

        return installations;
    }

    private static IReadOnlyList<DedicatedServerInstallation> DiscoverDedicatedServers(
        IEnumerable<string> steamLibraries,
        StringComparer comparer)
    {
        var servers = new List<DedicatedServerInstallation>();
        var seenRoots = new HashSet<string>(comparer);

        foreach (var steamLibrary in steamLibraries)
        {
            var serverRoot = Path.Combine(steamLibrary, "steamapps", "common", "PalServer");
            var serverExecutable = FindDedicatedServerExecutable(serverRoot);
            if (serverExecutable is null)
            {
                continue;
            }

            var normalizedServerRoot = NormalizePathOrNull(serverRoot);
            if (normalizedServerRoot is null || !seenRoots.Add(normalizedServerRoot))
            {
                continue;
            }

            servers.Add(new DedicatedServerInstallation(
                LibraryPath: steamLibrary,
                RootPath: normalizedServerRoot,
                ExecutablePath: serverExecutable));
        }

        return servers;
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

    private sealed record DedicatedServerInstallation(
        string LibraryPath,
        string RootPath,
        string ExecutablePath);

    [GeneratedRegex("\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamLibraryPathRegex();
}
