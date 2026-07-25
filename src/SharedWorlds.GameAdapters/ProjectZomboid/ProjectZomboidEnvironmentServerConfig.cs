using System.Globalization;
using SharedWorlds.Core.Abstractions;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal static partial class ProjectZomboidEnvironment
{
    private static ServerModConfiguration ReadServerModConfiguration(
        GameInstallation installation,
        string serverName)
    {
        var userDataRoot = GetRequiredUserDataRoot(installation);
        var serverConfigRoot = Path.Combine(userDataRoot, "Server");
        RequireRegularDirectory(
            serverConfigRoot,
            "Project Zomboid Server configuration directory");
        var configPath = Path.Combine(serverConfigRoot, serverName + ".ini");
        RequireRegularFile(
            configPath,
            "Project Zomboid server definition");

        string[] lines;
        try
        {
            lines = File.ReadAllLines(configPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Project Zomboid server definition could not be read: {exception.Message}",
                exception);
        }

        var workshopRaw = ReadUniqueIniValue(lines, "WorkshopItems", configPath) ?? string.Empty;
        var modsRaw = ReadUniqueIniValue(lines, "Mods", configPath) ?? string.Empty;
        return new ServerModConfiguration(
            ParseWorkshopIds(workshopRaw, configPath),
            ParseModIds(modsRaw));
    }

    private static string? ReadUniqueIniValue(
        IEnumerable<string> lines,
        string key,
        string path)
    {
        var prefix = key + "=";
        var matches = lines
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(line => line[prefix.Length..].Trim())
            .ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"Project Zomboid server definition contains multiple {key}= entries: {path}");
        }

        return matches.SingleOrDefault();
    }

    private static IReadOnlyList<string> ParseWorkshopIds(string value, string path)
    {
        var ids = value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!ulong.TryParse(
                    id,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var numericId) ||
                numericId == 0)
            {
                throw new InvalidOperationException(
                    $"Project Zomboid server definition contains invalid WorkshopItems ID '{id}': {path}");
            }

            if (!seen.Add(id))
            {
                throw new InvalidOperationException(
                    $"Project Zomboid server definition contains duplicate WorkshopItems ID {id}: {path}");
            }
        }

        return ids;
    }

    private static IReadOnlyList<string> ParseModIds(string value)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in value.Split(
                     ';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = token.LastIndexOf('\\');
            var id = (separator >= 0 ? token[(separator + 1)..] : token).Trim();
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                continue;
            }

            ids.Add(id);
        }

        return ids;
    }

    private sealed record ServerModConfiguration(
        IReadOnlyList<string> WorkshopItemIds,
        IReadOnlyList<string> ModIds);
}
