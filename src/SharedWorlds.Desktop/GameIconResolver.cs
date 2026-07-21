using System.Text.RegularExpressions;
using Microsoft.Win32;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.Desktop;

/// <summary>
/// Resolves presentation artwork without teaching the desktop about individual games.
/// Steam artwork is preferred, then an adapter-provided path, then artwork shipped with the game.
/// </summary>
internal sealed partial class GameIconResolver
{
    private static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg", ".ico"];

    public string? Resolve(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);

        var explicitIcon = GetExistingMetadataPath(installation, "iconPath");
        if (explicitIcon is not null)
        {
            return explicitIcon;
        }

        var steamAppId = GetMetadataValue(installation, "gameSteamAppId")
            ?? GetMetadataValue(installation, "steamAppId")
            ?? TryResolveSteamAppIdFromManifest(installation);

        if (!string.IsNullOrWhiteSpace(steamAppId))
        {
            var steamArtwork = FindSteamArtwork(installation, steamAppId);
            if (steamArtwork is not null)
            {
                return steamArtwork;
            }
        }

        return FindGameProvidedArtwork(installation.RootPath);
    }

    private static string? FindSteamArtwork(GameInstallation installation, string appId)
    {
        foreach (var steamRoot in DiscoverSteamRoots(installation))
        {
            var libraryCache = Path.Combine(steamRoot, "appcache", "librarycache");
            if (!Directory.Exists(libraryCache))
            {
                continue;
            }

            var candidates = new List<string>();
            AddExistingFile(candidates, Path.Combine(libraryCache, $"{appId}_icon.png"));
            AddExistingFile(candidates, Path.Combine(libraryCache, $"{appId}_icon.jpg"));
            AddExistingFile(candidates, Path.Combine(libraryCache, $"{appId}.png"));
            AddExistingFile(candidates, Path.Combine(libraryCache, $"{appId}.jpg"));

            AddFilesSafe(candidates, libraryCache, $"{appId}_*", SearchOption.TopDirectoryOnly);

            var appDirectory = Path.Combine(libraryCache, appId);
            AddFilesSafe(candidates, appDirectory, "*", SearchOption.TopDirectoryOnly);

            var selected = candidates
                .Where(IsSupportedArtwork)
                .Distinct(GetPathComparer())
                .OrderBy(GetArtworkPreference)
                .ThenBy(path => path.Length)
                .FirstOrDefault();
            if (selected is not null)
            {
                return Path.GetFullPath(selected);
            }
        }

        return null;
    }

    private static string? FindGameProvidedArtwork(string installationRoot)
    {
        if (!Directory.Exists(installationRoot))
        {
            return null;
        }

        var candidates = new List<string>();
        AddFilesSafe(candidates, installationRoot, "*", SearchOption.TopDirectoryOnly);

        foreach (var directory in EnumerateDirectoriesSafe(installationRoot).Take(24))
        {
            AddFilesSafe(candidates, directory, "*", SearchOption.TopDirectoryOnly);
        }

        return candidates
            .Where(IsSupportedArtwork)
            .OrderBy(GetArtworkPreference)
            .ThenBy(path => path.Count(character => character == Path.DirectorySeparatorChar))
            .ThenBy(path => path.Length)
            .Select(Path.GetFullPath)
            .FirstOrDefault();
    }

    private static int GetArtworkPreference(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Contains("icon", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (name.Contains("logo", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (name.Contains("small", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (name.Contains("header", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("capsule", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("library", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        return 3;
    }

    private static string? TryResolveSteamAppIdFromManifest(GameInstallation installation)
    {
        var steamAppsPath = FindSteamAppsAncestor(installation.RootPath);
        if (steamAppsPath is null)
        {
            return null;
        }

        var comparer = GetPathComparer();
        foreach (var manifestPath in EnumerateFilesSafe(steamAppsPath, "appmanifest_*.acf"))
        {
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

            var installDirectoryMatch = SteamInstallDirectoryRegex().Match(text);
            if (!installDirectoryMatch.Success)
            {
                continue;
            }

            var installDirectory = installDirectoryMatch.Groups[1].Value
                .Replace("\\\\", "\\", StringComparison.Ordinal);
            var manifestRoot = Path.GetFullPath(Path.Combine(steamAppsPath, "common", installDirectory));
            if (!comparer.Equals(manifestRoot, Path.GetFullPath(installation.RootPath)))
            {
                continue;
            }

            var fileName = Path.GetFileNameWithoutExtension(manifestPath);
            const string prefix = "appmanifest_";
            return fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? fileName[prefix.Length..]
                : null;
        }

        return null;
    }

    private static IEnumerable<string> DiscoverSteamRoots(GameInstallation installation)
    {
        var roots = new HashSet<string>(GetPathComparer());

        var steamAppsPath = FindSteamAppsAncestor(installation.RootPath);
        if (steamAppsPath is not null)
        {
            var libraryRoot = Directory.GetParent(steamAppsPath)?.FullName;
            if (!string.IsNullOrWhiteSpace(libraryRoot))
            {
                roots.Add(libraryRoot);
            }
        }

        if (OperatingSystem.IsWindows())
        {
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
                // Steam registry discovery is optional; other sources remain available.
            }
        }

        return roots.Where(Directory.Exists);
    }

    private static string? FindSteamAppsAncestor(string path)
    {
        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        while (current is not null)
        {
            if (string.Equals(current.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string? GetExistingMetadataPath(GameInstallation installation, string key)
    {
        var value = GetMetadataValue(installation, key);
        return !string.IsNullOrWhiteSpace(value) && File.Exists(value)
            ? Path.GetFullPath(value)
            : null;
    }

    private static string? GetMetadataValue(GameInstallation installation, string key)
    {
        return installation.Metadata is not null &&
               installation.Metadata.TryGetValue(key, out var value) &&
               !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static bool IsSupportedArtwork(string path)
    {
        return File.Exists(path) &&
               SupportedExtensions.Contains(
                   Path.GetExtension(path),
                   StringComparer.OrdinalIgnoreCase);
    }

    private static void AddExistingFile(ICollection<string> results, string path)
    {
        if (File.Exists(path))
        {
            results.Add(path);
        }
    }

    private static void AddFilesSafe(
        ICollection<string> results,
        string path,
        string searchPattern,
        SearchOption searchOption)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, searchPattern, searchOption))
                {
                    results.Add(file);
                }
            }
        }
        catch (IOException)
        {
            // Artwork is optional; inaccessible cache entries are skipped.
        }
        catch (UnauthorizedAccessException)
        {
            // Artwork is optional; inaccessible cache entries are skipped.
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateDirectories(path).ToArray()
                : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string path, string searchPattern)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateFiles(path, searchPattern, SearchOption.TopDirectoryOnly).ToArray()
                : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [GeneratedRegex(
        "\\\"installdir\\\"\\s+\\\"([^\\\"]+)\\\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamInstallDirectoryRegex();
}
