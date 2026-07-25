using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal static partial class ProjectZomboidEnvironment
{
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
}
