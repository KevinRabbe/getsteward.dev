using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.Factorio;

internal static partial class FactorioInstallationDiscovery
{
    internal const string ExecutablePathKey = "executablePath";
    internal const string UserDataPathKey = "userDataPath";

    public static IReadOnlyList<GameInstallation> Discover()
    {
        var candidates = new List<(string RootPath, string Source)>();

        foreach (var steamLibrary in DiscoverSteamLibraries())
        {
            candidates.Add((Path.Combine(steamLibrary, "steamapps", "common", "Factorio"), "steam"));
        }

        foreach (var standalone in DiscoverConventionalStandalonePaths())
        {
            candidates.Add((standalone, "standalone"));
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        var seen = new HashSet<string>(comparer);
        var installations = new List<GameInstallation>();

        foreach (var (candidateRoot, source) in candidates)
        {
            string root;
            try
            {
                root = Path.GetFullPath(candidateRoot);
            }
            catch
            {
                continue;
            }

            if (!seen.Add(root))
            {
                continue;
            }

            var executable = FindExecutable(root);
            if (executable is null)
            {
                continue;
            }

            var userDataPath = ResolveUserDataPath(root);
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ExecutablePathKey] = executable,
                [UserDataPathKey] = userDataPath
            };

            installations.Add(new GameInstallation(
                Id: $"factorio:{root}",
                RootPath: root,
                Source: source,
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
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(Path.Combine(home, "Library", "Application Support", "Steam"));
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
                // A locked or temporarily unavailable Steam metadata file should not break discovery.
            }
            catch (UnauthorizedAccessException)
            {
                // Discovery is best-effort; inaccessible libraries are simply skipped.
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

    private static IEnumerable<string> DiscoverConventionalStandalonePaths()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                yield return Path.Combine(programFiles, "Factorio");
            }

            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                yield return Path.Combine(programFilesX86, "Factorio");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            yield return Path.Combine(home, ".factorio");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(Path.DirectorySeparatorChar.ToString(), "Applications", "factorio.app", "Contents");
        }
    }

    private static string? FindExecutable(string root)
    {
        string[] candidates;

        if (OperatingSystem.IsWindows())
        {
            candidates = [Path.Combine(root, "bin", "x64", "factorio.exe")];
        }
        else if (OperatingSystem.IsLinux())
        {
            candidates = [Path.Combine(root, "bin", "x64", "factorio")];
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates =
            [
                Path.Combine(root, "factorio.app", "Contents", "MacOS", "factorio"),
                Path.Combine(root, "MacOS", "factorio")
            ];
        }
        else
        {
            return null;
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string ResolveUserDataPath(string installationRoot)
    {
        // Factorio's portable ZIP distribution keeps saves/mods beside the installation.
        if (Directory.Exists(Path.Combine(installationRoot, "saves")))
        {
            return installationRoot;
        }

        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Factorio");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "Application Support", "factorio");
        }

        return Path.Combine(home, ".factorio");
    }

    [GeneratedRegex("\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamLibraryPathRegex();
}
