using System.Globalization;
using System.Runtime.CompilerServices;

namespace SharedWorlds.Desktop;

/// <summary>
/// Bridges the SafeWorld-named Steam package contract into the already-qualified internal parser.
/// This is process-local compatibility only; public beta packages no longer need the old filename.
/// </summary>
internal static class SafeWorldSteamCompatibilityBootstrap
{
    private const string LegacySteamAppIdVariable = "STEWARD_STEAM_APP_ID";
    private const string InvalidConfiguration = "invalid-mixed-safeworld-steam-configuration";

    [ModuleInitializer]
    internal static void Initialize()
    {
        var canonicalPath = Path.Combine(
            AppContext.BaseDirectory,
            SafeWorldDesktopSteamConfiguration.PackageConfigurationFileName);
        var canonicalEnvironment = Environment.GetEnvironmentVariable(
            SafeWorldDesktopSteamConfiguration.SteamAppIdVariable);
        if (!File.Exists(canonicalPath) && string.IsNullOrWhiteSpace(canonicalEnvironment))
        {
            return;
        }

        if (!SafeWorldDesktopSteamConfiguration.TryLoad(
                out var configuration,
                out _) ||
            configuration is null)
        {
            Environment.SetEnvironmentVariable(
                LegacySteamAppIdVariable,
                InvalidConfiguration);
            return;
        }

        Environment.SetEnvironmentVariable(
            LegacySteamAppIdVariable,
            configuration.AppId.ToString(CultureInfo.InvariantCulture));
    }
}
