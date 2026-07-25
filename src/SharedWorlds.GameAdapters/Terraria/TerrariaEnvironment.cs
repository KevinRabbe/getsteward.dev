using System.Text;
using System.Text.RegularExpressions;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Terraria;

internal static partial class TerrariaEnvironment
{
    internal const long MaximumSteamManifestBytes = 4L * 1024 * 1024;

    public static EnvironmentManifest Inspect(GameInstallation installation)
        => new(
            SchemaVersion: 1,
            AdapterId: "terraria",
            GameVersion: ReadRequiredBuildId(installation),
            Components: [],
            Configuration: new Dictionary<string, string>(StringComparer.Ordinal));

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
                "terraria-environment-schema-unsupported",
                $"Terraria environment schema {requiredEnvironment.SchemaVersion} is not supported."));
        }

        if (!string.Equals(requiredEnvironment.AdapterId, "terraria", StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "terraria-adapter-mismatch",
                $"The required environment belongs to adapter '{requiredEnvironment.AdapterId}', not Terraria."));
        }

        if (requiredEnvironment.Components.Count != 0)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "terraria-environment-components-unsupported",
                "Vanilla Terraria environment revisions do not declare adapter-managed components."));
        }

        string? installedBuildId = null;
        try
        {
            installedBuildId = ReadRequiredBuildId(installation);
        }
        catch (InvalidOperationException exception)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "terraria-build-unavailable",
                exception.Message));
        }

        if (installedBuildId is not null &&
            !string.Equals(installedBuildId, requiredEnvironment.GameVersion, StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "terraria-version-mismatch",
                $"This World requires Terraria Steam build {requiredEnvironment.GameVersion}, but this device has build {installedBuildId}."));
        }

        return issues.Count == 0
            ? EnvironmentVerificationReport.Ready()
            : EnvironmentVerificationReport.Blocked(issues.ToArray());
    }

    internal static void RequireCompatible(
        GameInstallation installation,
        EnvironmentManifest requiredEnvironment)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(requiredEnvironment);
        if (requiredEnvironment.SchemaVersion != 1 ||
            !string.Equals(requiredEnvironment.AdapterId, "terraria", StringComparison.Ordinal) ||
            requiredEnvironment.Components.Count != 0)
        {
            throw new InvalidOperationException(
                "The required environment is not a supported vanilla Terraria environment revision.");
        }

        var installedBuildId = ReadRequiredBuildId(installation);
        if (!string.Equals(installedBuildId, requiredEnvironment.GameVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Terraria Steam build {installedBuildId} does not match required build {requiredEnvironment.GameVersion}.");
        }
    }

    internal static string ReadRequiredBuildId(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                TerrariaInstallationDiscovery.SteamManifestPathKey,
                out var manifestPath) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException(
                "Terraria's Steam manifest is not available on this device, so Steward cannot verify an exact game build.");
        }

        var fullPath = Path.GetFullPath(manifestPath);
        FileStream stream;
        try
        {
            stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Terraria's Steam manifest could not be opened safely: {fullPath}",
                exception);
        }

        using (stream)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(fullPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Terraria's Steam manifest ownership could not be inspected safely: {fullPath}",
                    exception);
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) != 0)
            {
                throw new InvalidOperationException(
                    $"Terraria's Steam manifest is not a regular owned file: {fullPath}");
            }

            if (stream.Length > MaximumSteamManifestBytes)
            {
                throw new InvalidOperationException(
                    $"Terraria's Steam manifest exceeds Steward's {MaximumSteamManifestBytes}-byte metadata safety limit: {fullPath}");
            }

            var maximum = checked((int)MaximumSteamManifestBytes);
            var bytes = new byte[maximum + 1];
            var total = 0;
            while (total < bytes.Length)
            {
                var read = stream.Read(bytes, total, bytes.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > maximum)
            {
                throw new InvalidOperationException(
                    $"Terraria's Steam manifest exceeded Steward's {MaximumSteamManifestBytes}-byte metadata safety limit while being read: {fullPath}");
            }

            var text = Encoding.UTF8.GetString(bytes, 0, total);
            var match = SteamBuildIdRegex().Match(text);
            if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                throw new InvalidOperationException(
                    $"Steam buildid was not found in Terraria's manifest: {fullPath}");
            }

            return match.Groups[1].Value;
        }
    }

    [GeneratedRegex("\\\"buildid\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamBuildIdRegex();
}
