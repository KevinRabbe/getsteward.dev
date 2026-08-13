using System.IO;

namespace SharedWorlds.Desktop;

/// <summary>
/// SafeWorld release-facing Steam configuration boundary. New packages use SafeWorld names while
/// older engineering packages remain readable through the qualified legacy parser.
/// </summary>
internal sealed record SafeWorldDesktopSteamConfiguration(uint AppId)
{
    internal const string PackageConfigurationFileName = "safeworld-steam.json";
    internal const string SteamAppIdVariable = "SAFEWORLD_STEAM_APP_ID";
    private const string LegacyPackageConfigurationFileName = "steward-steam.json";
    private const string LegacySteamAppIdVariable = "STEWARD_STEAM_APP_ID";

    public static bool TryLoad(
        out SafeWorldDesktopSteamConfiguration? configuration,
        out string? problem)
    {
        configuration = null;

        var canonicalPath = Path.Combine(
            AppContext.BaseDirectory,
            PackageConfigurationFileName);
        var canonicalEnvironmentAppId = Environment.GetEnvironmentVariable(
            SteamAppIdVariable);
        var legacyPath = Path.Combine(
            AppContext.BaseDirectory,
            LegacyPackageConfigurationFileName);
        var legacyEnvironmentAppId = Environment.GetEnvironmentVariable(
            LegacySteamAppIdVariable);

        var configuredSourceCount = 0;
        configuredSourceCount += File.Exists(canonicalPath) ? 1 : 0;
        configuredSourceCount += string.IsNullOrWhiteSpace(canonicalEnvironmentAppId) ? 0 : 1;
        configuredSourceCount += File.Exists(legacyPath) ? 1 : 0;
        configuredSourceCount += string.IsNullOrWhiteSpace(legacyEnvironmentAppId) ? 0 : 1;
        if (configuredSourceCount > 1)
        {
            problem =
                "Steam platform configuration is ambiguous. Configure exactly one SafeWorld or legacy AppID source.";
            return false;
        }

        StewardDesktopSteamConfiguration? parsed;
        if (File.Exists(canonicalPath) ||
            !string.IsNullOrWhiteSpace(canonicalEnvironmentAppId))
        {
            if (!StewardDesktopSteamConfiguration.TryLoad(
                canonicalPath,
                canonicalEnvironmentAppId,
                out parsed,
                out problem))
            {
                return false;
            }
        }
        else
        {
            if (!StewardDesktopSteamConfiguration.TryLoad(
                out parsed,
                out problem))
            {
                return false;
            }
        }

        if (parsed is null)
        {
            problem = null;
            return false;
        }

        configuration = new SafeWorldDesktopSteamConfiguration(parsed.AppId);
        problem = null;
        return true;
    }
}
