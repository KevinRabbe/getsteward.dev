using System.Text.RegularExpressions;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal static partial class ProjectZomboidEnvironment
{
    private const int MaxModInfoFilesPerWorkshopItem = 512;
    private const int MaxDirectoriesPerWorkshopItem = 16_384;

    private static Dictionary<string, InstalledWorkshopItem> ReadInstalledWorkshopItems(
        GameInstallation installation)
    {
        var serverRoot = GetRequiredDedicatedServerRoot(installation);
        var steamAppsRoot = Path.Combine(serverRoot, "steamapps");
        if (!TryRequireRegularDirectory(
                steamAppsRoot,
                "Project Zomboid Dedicated Server steamapps directory"))
        {
            return new Dictionary<string, InstalledWorkshopItem>(StringComparer.Ordinal);
        }

        var workshopRoot = Path.Combine(steamAppsRoot, "workshop");
        if (!TryRequireRegularDirectory(
                workshopRoot,
                "Project Zomboid Workshop directory"))
        {
            return new Dictionary<string, InstalledWorkshopItem>(StringComparer.Ordinal);
        }

        var manifestPath = Path.Combine(workshopRoot, "appworkshop_108600.acf");
        if (!TryRequireRegularFile(
                manifestPath,
                "Project Zomboid Workshop manifest"))
        {
            return new Dictionary<string, InstalledWorkshopItem>(StringComparer.Ordinal);
        }

        string text;
        try
        {
            text = File.ReadAllText(manifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Project Zomboid Workshop manifest could not be read: {exception.Message}",
                exception);
        }

        var installedSection = ExtractKeyValuesObject(text, "WorkshopItemsInstalled");
        if (installedSection is null)
        {
            return new Dictionary<string, InstalledWorkshopItem>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, InstalledWorkshopItem>(StringComparer.Ordinal);
        foreach (Match match in WorkshopItemBlockRegex().Matches(installedSection))
        {
            var workshopId = match.Groups["id"].Value;
            var body = match.Groups["body"].Value;
            var manifest = WorkshopManifestRegex().Match(body);
            if (!manifest.Success || string.IsNullOrWhiteSpace(manifest.Groups[1].Value))
            {
                throw new InvalidOperationException(
                    $"Project Zomboid Workshop item {workshopId} has no Steam content manifest identity in {manifestPath}.");
            }

            var manifestId = manifest.Groups[1].Value;
            var contentRoot = Path.Combine(workshopRoot, "content");
            RequireRegularDirectory(
                contentRoot,
                "Project Zomboid Workshop content directory");
            var appContentRoot = Path.Combine(contentRoot, "108600");
            RequireRegularDirectory(
                appContentRoot,
                "Project Zomboid Workshop app content directory");
            var contentPath = Path.Combine(appContentRoot, workshopId);
            RequireRegularDirectory(
                contentPath,
                $"Project Zomboid Workshop item {workshopId} content directory");

            if (!result.TryAdd(
                    workshopId,
                    new InstalledWorkshopItem(manifestId, Path.GetFullPath(contentPath))))
            {
                throw new InvalidOperationException(
                    $"Project Zomboid Workshop manifest contains duplicate installed item {workshopId}.");
            }
        }

        return result;
    }

    internal static IReadOnlyCollection<string> ReadWorkshopModIds(
        string contentPath,
        string workshopId)
    {
        string[] modInfoFiles;
        try
        {
            modInfoFiles = EnumerateWorkshopModInfoFiles(contentPath, workshopId)
                .Take(MaxModInfoFilesPerWorkshopItem + 1)
                .ToArray();
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException(
                $"Project Zomboid Workshop item {workshopId} content could not be inspected: {exception.Message}",
                exception);
        }

        if (modInfoFiles.Length > MaxModInfoFilesPerWorkshopItem)
        {
            throw new InvalidOperationException(
                $"Project Zomboid Workshop item {workshopId} contains more than {MaxModInfoFilesPerWorkshopItem} mod.info files; Steward stopped instead of performing an unbounded scan.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var modInfoPath in modInfoFiles)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(modInfoPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Project Zomboid mod metadata could not be read: {modInfoPath}: {exception.Message}",
                    exception);
            }

            var idValues = lines
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("id=", StringComparison.OrdinalIgnoreCase))
                .Select(line => line[3..].Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (idValues.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Project Zomboid mod metadata declares multiple IDs: {modInfoPath}");
            }

            if (idValues.Length == 1)
            {
                ids.Add(idValues[0]);
            }
        }

        return ids;
    }

    private static IEnumerable<string> EnumerateWorkshopModInfoFiles(
        string contentPath,
        string workshopId)
    {
        var root = Path.GetFullPath(contentPath);
        RejectWorkshopReparsePoint(root, workshopId);

        var pending = new Stack<string>();
        pending.Push(root);
        var inspectedDirectories = 0;

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            inspectedDirectories++;
            if (inspectedDirectories > MaxDirectoriesPerWorkshopItem)
            {
                throw new InvalidOperationException(
                    $"Project Zomboid Workshop item {workshopId} contains more than {MaxDirectoriesPerWorkshopItem} directories; Steward stopped instead of performing an unbounded scan.");
            }

            foreach (var directory in Directory.EnumerateDirectories(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                RejectWorkshopReparsePoint(directory, workshopId);
                pending.Push(directory);
            }

            foreach (var modInfoPath in Directory.EnumerateFiles(
                         current,
                         "mod.info",
                         SearchOption.TopDirectoryOnly))
            {
                RejectWorkshopReparsePoint(modInfoPath, workshopId);
                yield return modInfoPath;
            }
        }
    }

    private static void RejectWorkshopReparsePoint(string path, string workshopId)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Project Zomboid Workshop item {workshopId} contains a linked or reparse-point path that Steward will not inspect: '{path}'.");
        }
    }

    private static string? ExtractKeyValuesObject(string text, string key)
    {
        var keyToken = '"' + key + '"';
        var keyIndex = text.IndexOf(keyToken, StringComparison.OrdinalIgnoreCase);
        if (keyIndex < 0)
        {
            return null;
        }

        var openBrace = text.IndexOf('{', keyIndex + keyToken.Length);
        if (openBrace < 0)
        {
            throw new InvalidOperationException(
                $"Project Zomboid Workshop manifest has malformed {key} section.");
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = openBrace; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                continue;
            }

            if (character == '{')
            {
                depth++;
            }
            else if (character == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[(openBrace + 1)..index];
                }
            }
        }

        throw new InvalidOperationException(
            $"Project Zomboid Workshop manifest has unterminated {key} section.");
    }

    [GeneratedRegex("\\\"(?<id>[0-9]+)\\\"\\s*\\{(?<body>.*?)\\}", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex WorkshopItemBlockRegex();

    [GeneratedRegex("\\\"manifest\\\"\\s+\\\"([0-9]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkshopManifestRegex();

    private sealed record InstalledWorkshopItem(
        string ManifestId,
        string ContentPath);
}
