using System.Globalization;
using System.Runtime.CompilerServices;

namespace SharedWorlds.Desktop;

/// <summary>
/// Bridges the SafeWorld-named Steam package contract into the already-qualified internal parser.
/// This is process-local compatibility only; public beta packages no longer need the old filename.
/// </summary>
internal static class SafeWorldSteamCompatibilityBootstrap
{
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
                StewardDesktopSteamConfiguration.SteamAppIdVariable,
                InvalidConfiguration);
            return;
        }

        Environment.SetEnvironmentVariable(
            StewardDesktopSteamConfiguration.SteamAppIdVariable,
            configuration.AppId.ToString(CultureInfo.InvariantCulture));
    }
}
