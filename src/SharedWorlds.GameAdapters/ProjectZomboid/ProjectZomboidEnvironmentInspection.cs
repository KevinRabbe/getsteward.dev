using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal static partial class ProjectZomboidEnvironment
{
    private const string WorkshopComponentKind = "steam-workshop";

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
}
