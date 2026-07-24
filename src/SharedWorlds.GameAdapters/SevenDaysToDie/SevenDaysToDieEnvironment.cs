using System.Text.RegularExpressions;
using System.Xml.Linq;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal static partial class SevenDaysToDieEnvironment
{
    public static EnvironmentManifest Inspect(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var buildId = ReadRequiredDedicatedServerBuildId(installation);
        var mods = ReadDedicatedServerMods(installation);
        return new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: "7-days-to-die",
            GameVersion: buildId,
            Components: mods,
            Configuration: new Dictionary<string, string>(StringComparer.Ordinal));
    }

    public static EnvironmentVerificationReport Verify(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(requiredEnvironment);

        var issues = new List<EnvironmentVerificationIssue>();
        if (requiredEnvironment.SchemaVersion != 1)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "7dtd-environment-schema-unsupported",
                $"7 Days to Die environment schema {requiredEnvironment.SchemaVersion} is not supported."));
        }

        if (!string.Equals(requiredEnvironment.AdapterId, "7-days-to-die", StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "7dtd-adapter-mismatch",
                $"The required environment belongs to adapter '{requiredEnvironment.AdapterId}', not 7 Days to Die."));
        }

        string? installedBuildId = null;
        try
        {
            installedBuildId = ReadRequiredDedicatedServerBuildId(installation);
        }
        catch (InvalidOperationException exception)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "7dtd-dedicated-server-build-unavailable",
                exception.Message));
        }

        if (installedBuildId is not null &&
            !string.Equals(installedBuildId, requiredEnvironment.GameVersion, StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "7dtd-version-mismatch",
                $"This World requires 7 Days to Die Dedicated Server Steam build {requiredEnvironment.GameVersion}, but this device has build {installedBuildId}."));
        }

        IReadOnlyList<EnvironmentComponent>? installedMods = null;
        try
        {
            installedMods = ReadDedicatedServerMods(installation);
        }
        catch (InvalidOperationException exception)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "7dtd-mod-inventory-unavailable",
                exception.Message));
        }

        if (installedMods is not null)
        {
            CompareMods(requiredEnvironment.Components, installedMods, issues);
        }

        return issues.Count == 0
            ? EnvironmentVerificationReport.Ready()
            : EnvironmentVerificationReport.Blocked(issues.ToArray());
    }

    internal static string ReadRequiredDedicatedServerBuildId(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                SevenDaysToDieInstallationDiscovery.DedicatedServerManifestPathKey,
                out var manifestPath) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException(
                "7 Days to Die Dedicated Server is not installed with a discoverable Steam manifest on this device.");
        }

        var fullManifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullManifestPath))
        {
            throw new InvalidOperationException(
                $"7 Days to Die Dedicated Server Steam manifest was not found: {fullManifestPath}");
        }

        string text;
        try
        {
            text = File.ReadAllText(fullManifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"7 Days to Die Dedicated Server Steam manifest could not be read: {exception.Message}",
                exception);
        }

        var match = SteamBuildIdRegex().Match(text);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
        {
            throw new InvalidOperationException(
                $"Steam buildid was not found in 7 Days to Die Dedicated Server manifest: {fullManifestPath}");
        }

        return match.Groups[1].Value;
    }

    internal static IReadOnlyList<EnvironmentComponent> ReadDedicatedServerMods(GameInstallation installation)
    {
        var serverRoot = GetRequiredDedicatedServerRoot(installation);
        var modsRoot = Path.Combine(serverRoot, "Mods");
        if (!Directory.Exists(modsRoot))
        {
            return [];
        }

        var mods = new List<EnvironmentComponent>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(modsRoot, "*", SearchOption.TopDirectoryOnly).ToArray();
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
            var modInfoPath = Path.Combine(directory, "ModInfo.xml");
            if (!File.Exists(modInfoPath))
            {
                // ModInfo.xml is required for the game to recognize a mod folder.
                continue;
            }

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

    private static void CompareMods(
        IReadOnlyList<EnvironmentComponent> requiredComponents,
        IReadOnlyList<EnvironmentComponent> installedMods,
        ICollection<EnvironmentVerificationIssue> issues)
    {
        var unsupported = requiredComponents
            .Where(component => !string.Equals(component.Kind, "mod", StringComparison.Ordinal))
            .ToArray();
        foreach (var component in unsupported)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "7dtd-environment-component-unsupported",
                $"Required environment component '{component.Kind}:{component.Id}' is not understood by the 7 Days to Die adapter."));
        }

        var requiredMods = requiredComponents
            .Where(component => string.Equals(component.Kind, "mod", StringComparison.Ordinal))
            .ToDictionary(component => component.Id, StringComparer.Ordinal);
        var installedById = installedMods.ToDictionary(component => component.Id, StringComparer.Ordinal);

        foreach (var required in requiredMods.Values)
        {
            if (!installedById.TryGetValue(required.Id, out var installed))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "7dtd-mod-missing",
                    $"Required 7 Days to Die server mod '{required.Id}' is not installed."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(required.Version))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "7dtd-mod-version-unavailable",
                    $"Required 7 Days to Die server mod '{required.Id}' has no declared version, so exact reproduction cannot be verified."));
                continue;
            }

            if (!string.Equals(installed.Version, required.Version, StringComparison.Ordinal))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "7dtd-mod-version-mismatch",
                    $"Required 7 Days to Die server mod '{required.Id}' version {required.Version} does not match installed version {installed.Version ?? "(undeclared)"}."));
            }
        }

        foreach (var installed in installedMods)
        {
            if (!requiredMods.ContainsKey(installed.Id))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "7dtd-unexpected-mod",
                    $"7 Days to Die Dedicated Server has additional loaded mod '{installed.Id}' that is not part of this World environment."));
            }
        }
    }

    private static string GetRequiredDedicatedServerRoot(GameInstallation installation)
    {
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                SevenDaysToDieInstallationDiscovery.DedicatedServerRootPathKey,
                out var serverRoot) ||
            string.IsNullOrWhiteSpace(serverRoot))
        {
            throw new InvalidOperationException(
                "7 Days to Die Dedicated Server root is not available on this device.");
        }

        var fullRoot = Path.GetFullPath(serverRoot);
        if (!Directory.Exists(fullRoot))
        {
            throw new InvalidOperationException(
                $"7 Days to Die Dedicated Server root does not exist: {fullRoot}");
        }

        return fullRoot;
    }

    [GeneratedRegex("\\\"buildid\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamBuildIdRegex();
}
