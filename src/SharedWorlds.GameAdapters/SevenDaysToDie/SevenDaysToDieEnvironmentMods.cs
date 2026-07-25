using System.Xml.Linq;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal static partial class SevenDaysToDieEnvironment
{
    internal const int MaximumModDirectories = 1024;

    internal static IReadOnlyList<EnvironmentComponent> ReadDedicatedServerMods(GameInstallation installation)
    {
        var serverRoot = GetRequiredDedicatedServerRoot(installation);
        var modsRoot = Path.Combine(serverRoot, "Mods");
        if (!TryRequireRegularDirectory(
                modsRoot,
                "7 Days to Die Dedicated Server Mods directory"))
        {
            return [];
        }

        var mods = new List<EnvironmentComponent>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var directories = new List<string>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(
                         modsRoot,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (directories.Count >= MaximumModDirectories)
                {
                    throw new InvalidOperationException(
                        $"7 Days to Die Dedicated Server Mods directory contains more than Steward's {MaximumModDirectories}-directory environment inventory safety limit.");
                }

                directories.Add(directory);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException(
                $"7 Days to Die Dedicated Server Mods directory could not be read: {exception.Message}",
                exception);
        }

        foreach (var directory in directories.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            RequireRegularDirectory(
                directory,
                "7 Days to Die Dedicated Server mod directory");

            var modInfoPath = Path.Combine(directory, "ModInfo.xml");
            if (!TryRequireRegularFile(
                    modInfoPath,
                    "7 Days to Die ModInfo.xml"))
            {
                // ModInfo.xml is required for the game to recognize a mod folder.
                continue;
            }

            RequireFileWithinLimit(
                modInfoPath,
                "7 Days to Die ModInfo.xml",
                MaximumModInfoBytes);
            var (name, version) = ReadModInfo(modInfoPath);
            if (!names.Add(name))
            {
                throw new InvalidOperationException(
                    $"7 Days to Die Dedicated Server contains duplicate mod identity '{name}'.");
            }

            mods.Add(new EnvironmentComponent(
                Kind: "mod",
                Id: name,
                Version: version,
                Source: "dedicated-server"));
        }

        return mods
            .OrderBy(mod => mod.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static (string Name, string? Version) ReadModInfo(string path)
    {
        try
        {
            var document = XDocument.Load(path, LoadOptions.None);
            var root = document.Root
                ?? throw new InvalidOperationException(
                    $"7 Days to Die mod metadata has no XML root: {path}");
            var metadata = root.Element("ModInfo") ?? root;
            var name = ReadValueAttribute(metadata.Element("Name"));
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    $"7 Days to Die mod metadata has no declared Name: {path}");
            }

            var version = ReadValueAttribute(metadata.Element("Version"));
            return (name.Trim(), string.IsNullOrWhiteSpace(version) ? null : version.Trim());
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            throw new InvalidOperationException(
                $"7 Days to Die mod metadata could not be read safely: {path}: {exception.Message}",
                exception);
        }
    }

    private static string? ReadValueAttribute(XElement? element)
        => element?.Attribute("value")?.Value;
}
