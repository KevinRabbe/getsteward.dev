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

        foreach (var steamLibrary in DiscoverSteamLibraries())
        {
            var gameRoot = Path.Combine(steamLibrary, "steamapps", "common", "Palworld");
            var clientExecutable = FindClientExecutable(gameRoot);
            if (clientExecutable is null)
            {
                continue;
            }

            string normalizedGameRoot;
            try
            {
                normalizedGameRoot = Path.GetFullPath(gameRoot);
            }
            catch
            {
                continue;
            }

            if (!seen.Add(normalizedGameRoot))
            {
                continue;
            }

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ClientExecutablePathKey] = clientExecutable,
                [GameSteamAppIdKey] = GameSteamAppId,
                [DedicatedServerSteamAppIdKey] = DedicatedServerSteamAppId
            };

            var serverRoot = Path.Combine(steamLibrary, "steamapps", "common", "PalServer");
            var serverExecutable = FindDedicatedServerExecutable(serverRoot);
            if (serverExecutable is not null)
            {
                metadata[DedicatedServerRootPathKey] = Path.GetFullPath(serverRoot);
                metadata[DedicatedServerExecutablePathKey] = serverExecutable;
            }

            installations.Add(new GameInstallation(
                Id: $"palworld:{normalizedGameRoot}",
                RootPath: normalizedGameRoot,
                Source: "steam",
                Metadata: metadata));
        }

        return installations;
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

    [GeneratedRegex("\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamLibraryPathRegex();
}
