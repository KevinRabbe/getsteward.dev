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

    [GeneratedRegex(
        @"^(?<prefix>\s*DedicatedServerName\s*=\s*)[^\r\n]*",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DedicatedServerNameRegex();

    [GeneratedRegex(
        "\\\"buildid\\\"\\s+\\\"([0-9]+)\\\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamBuildIdRegex();
}
