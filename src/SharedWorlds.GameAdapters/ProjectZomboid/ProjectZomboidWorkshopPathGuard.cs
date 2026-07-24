using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal static class ProjectZomboidWorkshopPathGuard
{
    public static void ValidateConfiguredWorkshopItems(
        GameInstallation installation,
        DetectedWorld world)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(world);

        if (!TryGetWorkshopContentRoot(installation, out var workshopContentRoot) ||
            !TryGetUserDataRoot(installation, out var userDataRoot))
        {
            return;
        }

        var serverName = Path.GetFileName(Path.TrimEndingDirectorySeparator(world.SourcePath));
        if (string.IsNullOrWhiteSpace(serverName))
        {
            return;
        }

        var configPath = Path.Combine(userDataRoot, "Server", serverName + ".ini");
        if (!File.Exists(configPath))
        {
            return;
        }

        foreach (var workshopId in ReadConfiguredWorkshopIds(configPath))
        {
            ValidateWorkshopItem(workshopContentRoot, workshopId);
        }
    }

    public static void ValidateRequiredWorkshopItems(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(requiredEnvironment);

        if (!TryGetWorkshopContentRoot(installation, out var workshopContentRoot))
        {
            return;
        }

        foreach (var workshopId in requiredEnvironment.Mods
                     .Select(mod => mod.Metadata is not null &&
                                    mod.Metadata.TryGetValue("workshopId", out var value)
                         ? value
                         : null)
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.Ordinal))
        {
            ValidateWorkshopItem(workshopContentRoot, workshopId!);
        }
    }

    internal static void ValidateWorkshopItem(string workshopContentRoot, string workshopId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workshopContentRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workshopId);

        var itemRoot = Path.Combine(Path.GetFullPath(workshopContentRoot), workshopId);
        if (!Directory.Exists(itemRoot))
        {
            return;
        }

        RejectReparsePoint(itemRoot, workshopId);

        var pending = new Stack<string>();
        pending.Push(itemRoot);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                RejectReparsePoint(directory, workshopId);
                pending.Push(directory);
            }

            foreach (var filePath in Directory.EnumerateFiles(
                         current,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                RejectReparsePoint(filePath, workshopId);
            }
        }
    }

    private static IReadOnlyList<string> ReadConfiguredWorkshopIds(string configPath)
    {
        try
        {
            foreach (var rawLine in File.ReadLines(configPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0 ||
                    !string.Equals(
                        line[..separatorIndex].Trim(),
                        "WorkshopItems",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return line[(separatorIndex + 1)..]
                    .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Where(value => value.All(char.IsDigit))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        return [];
    }

    private static void RejectReparsePoint(string path, string workshopId)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not inspect Project Zomboid Workshop item '{workshopId}' path '{path}'.",
                exception);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Project Zomboid Workshop item '{workshopId}' contains a linked or reparse-point path that Steward will not inspect: '{path}'.");
        }
    }

    private static bool TryGetWorkshopContentRoot(
        GameInstallation installation,
        out string workshopContentRoot)
    {
        workshopContentRoot = string.Empty;
        return installation.Metadata is not null &&
               installation.Metadata.TryGetValue(
                   ProjectZomboidInstallationDiscovery.WorkshopContentRootKey,
                   out workshopContentRoot) &&
               !string.IsNullOrWhiteSpace(workshopContentRoot);
    }

    private static bool TryGetUserDataRoot(
        GameInstallation installation,
        out string userDataRoot)
    {
        userDataRoot = string.Empty;
        return installation.Metadata is not null &&
               installation.Metadata.TryGetValue(
                   ProjectZomboidInstallationDiscovery.UserDataPathKey,
                   out userDataRoot) &&
               !string.IsNullOrWhiteSpace(userDataRoot);
    }
}
