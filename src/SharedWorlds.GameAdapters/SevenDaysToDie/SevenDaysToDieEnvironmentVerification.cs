using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal static partial class SevenDaysToDieEnvironment
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

        var declaredRequiredMods = requiredComponents
            .Where(component => string.Equals(component.Kind, "mod", StringComparison.Ordinal))
            .ToArray();
        foreach (var invalid in declaredRequiredMods.Where(component => string.IsNullOrWhiteSpace(component.Id)))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "7dtd-mod-id-invalid",
                "Required 7 Days to Die server environment contains a mod with no usable identity."));
        }

        var validRequiredMods = declaredRequiredMods
            .Where(component => !string.IsNullOrWhiteSpace(component.Id))
            .ToArray();
        var requiredGroups = validRequiredMods
            .GroupBy(component => component.Id, StringComparer.Ordinal)
            .ToArray();
        foreach (var duplicate in requiredGroups.Where(group => group.Count() > 1))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "7dtd-required-mod-duplicate",
                $"Required 7 Days to Die server environment declares mod '{duplicate.Key}' more than once."));
        }

        var requiredMods = requiredGroups.ToDictionary(
            group => group.Key,
            group => group.First(),
            StringComparer.Ordinal);
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
}
