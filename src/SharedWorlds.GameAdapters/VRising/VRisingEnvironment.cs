using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.VRising;

internal static class VRisingEnvironment
{
    private static readonly string[] BepInExMarkers =
    [
        "BepInEx",
        "dotnet",
        ".doorstop_version",
        "doorstop_config.ini",
        "winhttp.dll"
    ];

    public static EnvironmentManifest Inspect(GameInstallation installation)
    {
        RequireVanillaInstallation(installation);
        return new EnvironmentManifest(
            1,
            "v-rising",
            ReadRequiredBuildId(installation),
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    public static EnvironmentVerificationReport Verify(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        ArgumentNullException.ThrowIfNull(requiredEnvironment);
        var issues = new List<EnvironmentVerificationIssue>();
        if (requiredEnvironment.SchemaVersion != 1)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "v-rising-environment-schema-unsupported",
                $"V Rising environment schema {requiredEnvironment.SchemaVersion} is not supported."));
        }

        if (!string.Equals(requiredEnvironment.AdapterId, "v-rising", StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "v-rising-adapter-mismatch",
                $"The required environment belongs to adapter '{requiredEnvironment.AdapterId}', not V Rising."));
        }

        if (requiredEnvironment.Components.Count != 0)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "v-rising-components-unsupported",
                "The current V Rising adapter supports only vanilla Worlds."));
        }

        try
        {
            RequireVanillaInstallation(installation);
            var build = ReadRequiredBuildId(installation);
            if (!string.Equals(build, requiredEnvironment.GameVersion, StringComparison.Ordinal))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "v-rising-version-mismatch",
                    $"This World requires V Rising Steam build {requiredEnvironment.GameVersion}, but this device has build {build}."));
            }
        }
        catch (InvalidOperationException ex)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "v-rising-environment-unavailable",
                ex.Message));
        }

        return issues.Count == 0
            ? EnvironmentVerificationReport.Ready()
            : EnvironmentVerificationReport.Blocked(issues.ToArray());
    }

    internal static void RequireCompatible(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        ArgumentNullException.ThrowIfNull(requiredEnvironment);
        if (requiredEnvironment.SchemaVersion != 1 ||
            !string.Equals(requiredEnvironment.AdapterId, "v-rising", StringComparison.Ordinal) ||
            requiredEnvironment.Components.Count != 0)
        {
            throw new InvalidOperationException(
                "The required environment is not a supported vanilla V Rising environment revision.");
        }

        RequireVanillaInstallation(installation);
        var installed = ReadRequiredBuildId(installation);
        if (!string.Equals(installed, requiredEnvironment.GameVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"V Rising Steam build {installed} does not match required build {requiredEnvironment.GameVersion}.");
        }
    }

    internal static void RequireVanillaInstallation(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var root = Path.GetFullPath(installation.RootPath);
        foreach (var markerName in BepInExMarkers)
        {
            var marker = Path.Combine(root, markerName);
            if (File.Exists(marker) || Directory.Exists(marker))
            {
                throw new InvalidOperationException(
                    $"V Rising's game root contains BepInEx marker '{markerName}'. Steward's current V Rising adapter is vanilla-only.");
            }
        }
    }

    internal static string ReadRequiredBuildId(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                VRisingInstallationDiscovery.SteamManifestPathKey,
                out var manifestPath) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException(
                "V Rising's Steam manifest is unavailable, so Steward cannot verify an exact game build.");
        }

        return VRisingSteamManifest.ReadRequired(manifestPath).BuildId;
    }
}
