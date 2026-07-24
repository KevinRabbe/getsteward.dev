using System.Text.RegularExpressions;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.SevenDaysToDie;

internal static partial class SevenDaysToDieEnvironment
{
    public static EnvironmentManifest Inspect(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var buildId = ReadRequiredBuildId(installation);
        return new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: "7-days-to-die",
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
            installedBuildId = ReadRequiredBuildId(installation);
        }
        catch (InvalidOperationException exception)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "7dtd-build-unavailable",
                exception.Message));
        }

        if (installedBuildId is not null &&
            !string.Equals(installedBuildId, requiredEnvironment.GameVersion, StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "7dtd-version-mismatch",
                $"This World requires 7 Days to Die Steam build {requiredEnvironment.GameVersion}, but this device has build {installedBuildId}."));
        }

        return issues.Count == 0
            ? EnvironmentVerificationReport.Ready()
            : EnvironmentVerificationReport.Blocked(issues.ToArray());
    }

    internal static string ReadRequiredBuildId(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        var manifestPath = ResolveClientManifestPath(installation.RootPath);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"7 Days to Die Steam manifest was not found: {manifestPath}");
        }

        string text;
        try
        {
            text = File.ReadAllText(manifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"7 Days to Die Steam manifest could not be read: {exception.Message}",
                exception);
        }

        var match = SteamBuildIdRegex().Match(text);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
        {
            throw new InvalidOperationException(
                $"Steam buildid was not found in 7 Days to Die manifest: {manifestPath}");
        }

        return match.Groups[1].Value;
    }

    internal static string ResolveClientManifestPath(string installationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationRoot);
        var root = Path.GetFullPath(installationRoot);
        var commonDirectory = Directory.GetParent(root)?.FullName
            ?? throw new InvalidOperationException(
                $"Could not determine the Steam common directory from 7 Days to Die install root: {root}");
        var steamAppsDirectory = Directory.GetParent(commonDirectory)?.FullName
            ?? throw new InvalidOperationException(
                $"Could not determine the Steam steamapps directory from 7 Days to Die install root: {root}");
        return Path.Combine(
            steamAppsDirectory,
            $"appmanifest_{SevenDaysToDieInstallationDiscovery.GameSteamAppId}.acf");
    }

    [GeneratedRegex("\\\"buildid\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamBuildIdRegex();
}
