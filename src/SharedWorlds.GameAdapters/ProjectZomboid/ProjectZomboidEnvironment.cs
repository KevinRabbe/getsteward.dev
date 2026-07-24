using System.Text.RegularExpressions;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal static partial class ProjectZomboidEnvironment
{
    private const string WorkshopComponentKind = "steam-workshop";
    private const int MaxModInfoFilesPerWorkshopItem = 512;

    public static EnvironmentManifest Inspect(
        GameInstallation installation,
        DetectedWorld world)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(world);

        var buildId = ReadRequiredDedicatedServerBuildId(installation);
        var serverName = GetServerName(world.SourcePath);
        var serverConfig = ReadServerModConfiguration(installation, serverName);
        var installedWorkshopItems = ReadInstalledWorkshopItems(installation);
        var selectedModIds = new HashSet<string>(StringComparer.Ordinal);
        var components = new List<EnvironmentComponent>();

        foreach (var workshopId in serverConfig.WorkshopItemIds)
        {
            if (!installedWorkshopItems.TryGetValue(workshopId, out var installed))
            {
                throw new InvalidOperationException(
                    $"Project Zomboid server '{serverName}' requires Workshop item {workshopId}, but Steam does not report that item as installed for the dedicated server.");
            }

            foreach (var modId in ReadWorkshopModIds(installed.ContentPath, workshopId))
            {
                selectedModIds.Add(modId);
            }

            components.Add(new EnvironmentComponent(
                WorkshopComponentKind,
                workshopId,
                installed.ManifestId,
                "steam-workshop"));
        }

        var unresolvedMods = serverConfig.ModIds
            .Where(modId => !selectedModIds.Contains(modId))
            .OrderBy(modId => modId, StringComparer.Ordinal)
            .ToArray();
        if (unresolvedMods.Length > 0)
        {
            throw new InvalidOperationException(
                $"Project Zomboid server '{serverName}' enables mod IDs that are not provided by its selected installed Workshop items: {string.Join(", ", unresolvedMods)}. Steward will not guess local or unresolved mod provenance.");
        }

        return new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: "project-zomboid",
            GameVersion: buildId,
            Components: components
                .OrderBy(component => component.Id, StringComparer.Ordinal)
                .ToArray(),
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
                "project-zomboid-environment-schema-unsupported",
                $"Project Zomboid environment schema {requiredEnvironment.SchemaVersion} is not supported."));
        }

        if (!string.Equals(requiredEnvironment.AdapterId, "project-zomboid", StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "project-zomboid-adapter-mismatch",
                $"The required environment belongs to adapter '{requiredEnvironment.AdapterId}', not Project Zomboid."));
        }

        string? installedBuildId = null;
        try
        {
            installedBuildId = ReadRequiredDedicatedServerBuildId(installation);
        }
        catch (InvalidOperationException exception)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "project-zomboid-dedicated-server-build-unavailable",
                exception.Message));
        }

        if (installedBuildId is not null &&
            !string.Equals(installedBuildId, requiredEnvironment.GameVersion, StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "project-zomboid-version-mismatch",
                $"This World requires Project Zomboid Dedicated Server Steam build {requiredEnvironment.GameVersion}, but this device has build {installedBuildId}."));
        }

        Dictionary<string, InstalledWorkshopItem>? installedWorkshopItems = null;
        try
        {
            installedWorkshopItems = ReadInstalledWorkshopItems(installation);
        }
        catch (InvalidOperationException exception)
        {
            if (requiredEnvironment.Components.Count > 0)
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "project-zomboid-workshop-inventory-unavailable",
                    exception.Message));
            }
        }

        foreach (var component in requiredEnvironment.Components)
        {
            if (!string.Equals(component.Kind, WorkshopComponentKind, StringComparison.Ordinal))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "project-zomboid-environment-component-unsupported",
                    $"Required environment component '{component.Kind}:{component.Id}' is not understood by the Project Zomboid adapter."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(component.Version))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "project-zomboid-workshop-manifest-unavailable",
                    $"Required Project Zomboid Workshop item {component.Id} has no exact Steam content manifest identity."));
                continue;
            }

            if (installedWorkshopItems is null ||
                !installedWorkshopItems.TryGetValue(component.Id, out var installed))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "project-zomboid-workshop-item-missing",
                    $"Required Project Zomboid Workshop item {component.Id} is not installed for the dedicated server."));
                continue;
            }

            if (!string.Equals(installed.ManifestId, component.Version, StringComparison.Ordinal))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "project-zomboid-workshop-manifest-mismatch",
                    $"Project Zomboid Workshop item {component.Id} requires Steam content manifest {component.Version}, but this device has {installed.ManifestId}."));
            }
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
                ProjectZomboidInstallationDiscovery.DedicatedServerManifestPathKey,
                out var manifestPath) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException(
                "Project Zomboid Dedicated Server is not installed with a discoverable Steam manifest on this device.");
        }

        var fullManifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullManifestPath))
        {
            throw new InvalidOperationException(
                $"Project Zomboid Dedicated Server Steam manifest was not found: {fullManifestPath}");
        }

        string text;
        try
        {
            text = File.ReadAllText(fullManifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Project Zomboid Dedicated Server Steam manifest could not be read: {exception.Message}",
                exception);
        }

        var match = SteamBuildIdRegex().Match(text);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
        {
            throw new InvalidOperationException(
                $"Steam buildid was not found in Project Zomboid Dedicated Server manifest: {fullManifestPath}");
        }

        return match.Groups[1].Value;
    }

    private static ServerModConfiguration ReadServerModConfiguration(
        GameInstallation installation,
        string serverName)
    {
        var userDataRoot = GetRequiredUserDataRoot(installation);
        var configPath = Path.Combine(userDataRoot, "Server", serverName + ".ini");
        if (!File.Exists(configPath))
        {
            throw new InvalidOperationException(
                $"Project Zomboid server definition was not found: {configPath}");
        }

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

    private static Dictionary<string, InstalledWorkshopItem> ReadInstalledWorkshopItems(
        GameInstallation installation)
    {
        var serverRoot = GetRequiredDedicatedServerRoot(installation);
        var workshopRoot = Path.Combine(serverRoot, "steamapps", "workshop");
        var manifestPath = Path.Combine(workshopRoot, "appworkshop_108600.acf");
        if (!File.Exists(manifestPath))
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
            var contentPath = Path.Combine(workshopRoot, "content", "108600", workshopId);
            if (!Directory.Exists(contentPath))
            {
                throw new InvalidOperationException(
                    $"Project Zomboid Workshop item {workshopId} is marked installed but its content directory is missing: {contentPath}");
            }

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

    private static IReadOnlyCollection<string> ReadWorkshopModIds(
        string contentPath,
        string workshopId)
    {
        string[] modInfoFiles;
        try
        {
            modInfoFiles = Directory
                .EnumerateFiles(contentPath, "mod.info", SearchOption.AllDirectories)
                .Take(MaxModInfoFilesPerWorkshopItem + 1)
                .ToArray();
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
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
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

    private static string GetRequiredDedicatedServerRoot(GameInstallation installation)
    {
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                ProjectZomboidInstallationDiscovery.DedicatedServerRootPathKey,
                out var serverRoot) ||
            string.IsNullOrWhiteSpace(serverRoot))
        {
            throw new InvalidOperationException(
                "Project Zomboid Dedicated Server root is not available on this device.");
        }

        var fullRoot = Path.GetFullPath(serverRoot);
        if (!Directory.Exists(fullRoot))
        {
            throw new InvalidOperationException(
                $"Project Zomboid Dedicated Server root does not exist: {fullRoot}");
        }

        return fullRoot;
    }

    private static string GetRequiredUserDataRoot(GameInstallation installation)
    {
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                ProjectZomboidInstallationDiscovery.UserDataPathKey,
                out var userDataRoot) ||
            string.IsNullOrWhiteSpace(userDataRoot))
        {
            throw new InvalidOperationException(
                "Project Zomboid installation is missing its user-data path.");
        }

        return Path.GetFullPath(userDataRoot);
    }

    private static string GetServerName(string worldPath)
    {
        var serverName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(worldPath)));
        if (string.IsNullOrWhiteSpace(serverName) ||
            serverName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException(
                $"Could not determine a safe Project Zomboid server name from '{worldPath}'.");
        }

        return serverName;
    }

    [GeneratedRegex("\\\"buildid\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamBuildIdRegex();

    [GeneratedRegex("\\\"(?<id>[0-9]+)\\\"\\s*\\{(?<body>.*?)\\}", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex WorkshopItemBlockRegex();

    [GeneratedRegex("\\\"manifest\\\"\\s+\\\"([0-9]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkshopManifestRegex();

    private sealed record ServerModConfiguration(
        IReadOnlyList<string> WorkshopItemIds,
        IReadOnlyList<string> ModIds);

    private sealed record InstalledWorkshopItem(
        string ManifestId,
        string ContentPath);
}
