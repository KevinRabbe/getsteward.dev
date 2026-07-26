using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.ConanExilesEnhanced;

internal static class ConanExilesEnhancedEnvironment
{
    internal const long MaximumModListBytes = 1024L * 1024;

    public static EnvironmentManifest Inspect(GameInstallation installation)
    {
        RequireVanilla(installation);
        return new EnvironmentManifest(
            1,
            "conan-exiles-enhanced",
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

        if (requiredEnvironment.SchemaVersion != 1 ||
            !string.Equals(requiredEnvironment.AdapterId, "conan-exiles-enhanced", StringComparison.Ordinal) ||
            requiredEnvironment.Components.Count != 0)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "conan-exiles-enhanced-environment-unsupported",
                "The requested Conan Exiles Enhanced environment is outside this adapter's vanilla state-only scope."));
        }

        try
        {
            RequireVanilla(installation);
            var installedBuild = ReadRequiredBuildId(installation);
            if (!string.Equals(installedBuild, requiredEnvironment.GameVersion, StringComparison.Ordinal))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "conan-exiles-enhanced-version-mismatch",
                    $"Required Steam build {requiredEnvironment.GameVersion}; installed build {installedBuild}."));
            }
        }
        catch (InvalidOperationException exception)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "conan-exiles-enhanced-environment-unavailable",
                exception.Message));
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
            !string.Equals(requiredEnvironment.AdapterId, "conan-exiles-enhanced", StringComparison.Ordinal) ||
            requiredEnvironment.Components.Count != 0)
        {
            throw new InvalidOperationException(
                "The required environment is not a supported vanilla Conan Exiles Enhanced revision.");
        }

        RequireVanilla(installation);
        var installedBuild = ReadRequiredBuildId(installation);
        if (!string.Equals(installedBuild, requiredEnvironment.GameVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Conan Exiles Enhanced Steam build {installedBuild} does not match required build {requiredEnvironment.GameVersion}.");
        }
    }

    internal static string ReadRequiredBuildId(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                ConanExilesEnhancedInstallationDiscovery.SteamManifestPathKey,
                out var manifestPath) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException(
                "Conan Exiles Enhanced's Steam manifest is unavailable.");
        }

        return ConanExilesEnhancedSteamManifest.ReadRequired(manifestPath).BuildId;
    }

    internal static void RequireVanilla(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                ConanExilesEnhancedInstallationDiscovery.ModsRootPathKey,
                out var modsRoot) ||
            !installation.Metadata.TryGetValue(
                ConanExilesEnhancedInstallationDiscovery.ModListPathKey,
                out var modListPath) ||
            string.IsNullOrWhiteSpace(modsRoot) ||
            string.IsNullOrWhiteSpace(modListPath))
        {
            throw new InvalidOperationException(
                "Conan Exiles Enhanced mod metadata is unavailable.");
        }

        var fullModsRoot = Path.GetFullPath(modsRoot);
        if (Directory.Exists(fullModsRoot) &&
            !ConanExilesEnhancedWorldDiscovery.IsRegularDirectory(fullModsRoot))
        {
            throw new InvalidOperationException(
                "Conan Exiles Enhanced's Mods directory is linked; the current adapter is vanilla-only.");
        }

        var fullModListPath = Path.GetFullPath(modListPath);
        if (!File.Exists(fullModListPath))
        {
            return;
        }

        var attributes = File.GetAttributes(fullModListPath);
        if ((attributes & FileAttributes.Directory) != 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Conan Exiles Enhanced's modlist is not a regular file; the current adapter is vanilla-only.");
        }

        var info = new FileInfo(fullModListPath);
        if (info.Length > MaximumModListBytes)
        {
            throw new InvalidOperationException(
                $"Conan Exiles Enhanced's modlist exceeds Steward's {MaximumModListBytes}-byte metadata limit.");
        }

        if (info.Length == 0)
        {
            return;
        }

        var text = File.ReadAllText(fullModListPath);
        if (!string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                "Conan Exiles Enhanced's modlist is non-empty; the current adapter is vanilla-only.");
        }
    }
}
