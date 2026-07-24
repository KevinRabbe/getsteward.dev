using System.Text.RegularExpressions;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.ProjectZomboid;

internal static partial class ProjectZomboidEnvironment
{
    public static EnvironmentManifest Inspect(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var buildId = ReadRequiredDedicatedServerBuildId(installation);
        return new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: "project-zomboid",
            GameVersion: buildId,
            Components: [],
            Configuration: new Dictionary<string, string>(StringComparer.Ordinal));
    }

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

        return issues.Count == 0
            ? EnvironmentVerificationReport.Ready()
            : EnvironmentVerificationReport.Blocked(issues.ToArray());
    }

    internal static string ReadRequiredDedicatedServerBuildId(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                ProjectZomboidInstallationDiscovery.DedicatedServerManifestPathKey,
                out var manifestPath) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException(
                "Project Zomboid Dedicated Server is not installed with a discoverable Steam manifest on this device.");
        }

        var fullManifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullManifestPath))
        {
            throw new InvalidOperationException(
                $"Project Zomboid Dedicated Server Steam manifest was not found: {fullManifestPath}");
        }

        string text;
        try
        {
            text = File.ReadAllText(fullManifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Project Zomboid Dedicated Server Steam manifest could not be read: {exception.Message}",
                exception);
        }

        var match = SteamBuildIdRegex().Match(text);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
        {
            throw new InvalidOperationException(
                $"Steam buildid was not found in Project Zomboid Dedicated Server manifest: {fullManifestPath}");
        }

        return match.Groups[1].Value;
    }

    [GeneratedRegex("\\\"buildid\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamBuildIdRegex();
}
