using System.Text.RegularExpressions;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Palworld;

internal static partial class PalworldDedicatedServerHosting
{
    private const string HostingModeKey = "hostingMode";
    private const string DedicatedServerNameKey = "dedicatedServerName";
    private const string DedicatedHostingMode = "dedicated-server";
    private const string UnknownGameVersion = "unknown";

    public static EnvironmentManifest InspectEnvironment(
        GameInstallation installation,
        DetectedWorld world)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(world);
        var worldId = GetWorldIdFromPath(world.SourcePath);
        return CreateDedicatedEnvironment(
            worldId,
            GetDedicatedServerBuildIdOrUnknown(installation));
    }

    public static EnvironmentVerificationReport VerifyEnvironment(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(requiredEnvironment);

        if (!OperatingSystem.IsWindows())
        {
            return EnvironmentVerificationReport.Unsupported(
                "The Palworld dedicated-host environment is currently validated only on Windows.");
        }

        var issues = new List<EnvironmentVerificationIssue>();

        if (requiredEnvironment.SchemaVersion != 1)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "palworld-environment-schema-unsupported",
                $"Palworld environment schema {requiredEnvironment.SchemaVersion} is not supported."));
        }

        if (!string.Equals(requiredEnvironment.AdapterId, "palworld", StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "palworld-adapter-mismatch",
                $"The required environment belongs to adapter '{requiredEnvironment.AdapterId}', not Palworld."));
        }

        if (!requiredEnvironment.Configuration.TryGetValue(HostingModeKey, out var hostingMode) ||
            !string.Equals(hostingMode, DedicatedHostingMode, StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "palworld-hosting-mode-mismatch",
                "The required Palworld environment is not the validated dedicated-server hosting mode."));
        }

        if (!requiredEnvironment.Configuration.TryGetValue(DedicatedServerNameKey, out var worldId) ||
            string.IsNullOrWhiteSpace(worldId))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "palworld-dedicated-world-missing",
                $"The required Palworld environment is missing '{DedicatedServerNameKey}'."));
        }
        else
        {
            try
            {
                ValidateWorldId(worldId);
            }
            catch (InvalidOperationException exception)
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "palworld-dedicated-world-invalid",
                    exception.Message));
            }
        }

        var serverAvailable = true;
        try
        {
            EnsureDedicatedServerAvailable(installation);
        }
        catch (InvalidOperationException exception)
        {
            serverAvailable = false;
            issues.Add(new EnvironmentVerificationIssue(
                "palworld-dedicated-server-missing",
                exception.Message));
        }

        if (string.IsNullOrWhiteSpace(requiredEnvironment.GameVersion) ||
            string.Equals(requiredEnvironment.GameVersion, UnknownGameVersion, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "palworld-exact-version-unavailable",
                "This World was captured without an exact Palworld dedicated-server Steam build ID. Steward will not guess that a different build is compatible."));
        }
        else if (!TryReadDedicatedServerBuildId(installation, out var currentBuildId, out var buildProblem))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "palworld-local-build-unavailable",
                buildProblem!));
        }
        else if (!string.Equals(requiredEnvironment.GameVersion, currentBuildId, StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "palworld-version-mismatch",
                $"This World requires Palworld dedicated-server Steam build {requiredEnvironment.GameVersion}, but this device has build {currentBuildId}."));
        }

        if (serverAvailable)
        {
            string configPath;
            try
            {
                configPath = GetDedicatedServerConfigPath(installation);
            }
            catch (InvalidOperationException exception)
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "palworld-server-config-unavailable",
                    exception.Message));
                configPath = string.Empty;
            }

            if (!string.IsNullOrEmpty(configPath))
            {
                if (!File.Exists(configPath))
                {
                    issues.Add(new EnvironmentVerificationIssue(
                        "palworld-server-not-initialized",
                        "PalServer has not initialized GameUserSettings.ini yet. Start the dedicated server once and stop it before hosting this World through Steward."));
                }
                else
                {
                    try
                    {
                        var configText = File.ReadAllText(configPath);
                        if (!DedicatedServerNameRegex().IsMatch(configText))
                        {
                            issues.Add(new EnvironmentVerificationIssue(
                                "palworld-server-config-incomplete",
                                $"DedicatedServerName was not found in PalServer configuration: {configPath}"));
                        }
                    }
                    catch (IOException exception)
                    {
                        issues.Add(new EnvironmentVerificationIssue(
                            "palworld-server-config-read-failed",
                            exception.Message));
                    }
                    catch (UnauthorizedAccessException exception)
                    {
                        issues.Add(new EnvironmentVerificationIssue(
                            "palworld-server-config-access-denied",
                            exception.Message));
                    }
                }
            }
        }

        return issues.Count == 0
            ? EnvironmentVerificationReport.Ready()
            : EnvironmentVerificationReport.Blocked(issues.ToArray());
    }

    private static EnvironmentManifest CreateDedicatedEnvironment(
        string worldId,
        string gameVersion)
    {
        ValidateWorldId(worldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);
        return new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: "palworld",
            GameVersion: gameVersion,
            Components: [],
            Configuration: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [HostingModeKey] = DedicatedHostingMode,
                [DedicatedServerNameKey] = worldId
            });
    }

    private static string GetDedicatedServerBuildIdOrUnknown(GameInstallation installation)
        => TryReadDedicatedServerBuildId(installation, out var buildId, out _)
            ? buildId!
            : UnknownGameVersion;

    private static bool TryReadDedicatedServerBuildId(
        GameInstallation installation,
        out string? buildId,
        out string? problem)
    {
        buildId = null;
        problem = null;

        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                PalworldInstallationDiscovery.DedicatedServerManifestPathKey,
                out var manifestPath) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            problem =
                "Steward cannot determine the exact Palworld dedicated-server build because its Steam app manifest was not discovered.";
            return false;
        }

        if (!File.Exists(manifestPath))
        {
            problem = $"The Palworld dedicated-server Steam app manifest is missing: {manifestPath}";
            return false;
        }

        string manifestText;
        try
        {
            manifestText = File.ReadAllText(manifestPath);
        }
        catch (IOException exception)
        {
            problem = exception.Message;
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            problem = exception.Message;
            return false;
        }

        var match = SteamBuildIdRegex().Match(manifestText);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
        {
            problem = $"Steam buildid was not found in Palworld dedicated-server manifest: {manifestPath}";
            return false;
        }

        buildId = match.Groups[1].Value;
        return true;
    }

    private static string GetDedicatedServerConfigPath(GameInstallation installation)
    {
        var serverRoot = GetRequiredMetadata(
            installation,
            PalworldInstallationDiscovery.DedicatedServerRootPathKey);
        return Path.Combine(
            serverRoot,
            "Pal",
            "Saved",
            "Config",
            "WindowsServer",
            "GameUserSettings.ini");
    }

    [GeneratedRegex(
        @"^(?<prefix>\s*DedicatedServerName\s*=\s*)[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DedicatedServerNameRegex();

    [GeneratedRegex(
        "\\\"buildid\\\"\\s+\\\"([0-9]+)\\\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamBuildIdRegex();
}
