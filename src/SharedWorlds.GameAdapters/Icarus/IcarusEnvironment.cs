using System.Text;
using System.Text.RegularExpressions;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Icarus;

internal static partial class IcarusEnvironment
{
    internal const long MaximumSteamManifestBytes = 4L * 1024 * 1024;

    public static EnvironmentManifest Inspect(GameInstallation installation)
    {
        RequireVanillaInstallation(installation);
        return new EnvironmentManifest(
            1,
            "icarus",
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
                "icarus-environment-schema-unsupported",
                $"ICARUS environment schema {requiredEnvironment.SchemaVersion} is not supported."));
        }

        if (!string.Equals(requiredEnvironment.AdapterId, "icarus", StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "icarus-adapter-mismatch",
                $"The required environment belongs to adapter '{requiredEnvironment.AdapterId}', not ICARUS."));
        }

        if (requiredEnvironment.Components.Count != 0)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "icarus-components-unsupported",
                "The current ICARUS adapter supports only vanilla Worlds."));
        }

        try
        {
            RequireVanillaInstallation(installation);
            var build = ReadRequiredBuildId(installation);
            if (!string.Equals(build, requiredEnvironment.GameVersion, StringComparison.Ordinal))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "icarus-version-mismatch",
                    $"This World requires ICARUS Steam build {requiredEnvironment.GameVersion}, but this device has build {build}."));
            }
        }
        catch (InvalidOperationException ex)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "icarus-environment-unavailable",
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
            !string.Equals(requiredEnvironment.AdapterId, "icarus", StringComparison.Ordinal) ||
            requiredEnvironment.Components.Count != 0)
        {
            throw new InvalidOperationException(
                "The required environment is not a supported vanilla ICARUS environment revision.");
        }

        RequireVanillaInstallation(installation);
        var installed = ReadRequiredBuildId(installation);
        if (!string.Equals(installed, requiredEnvironment.GameVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ICARUS Steam build {installed} does not match required build {requiredEnvironment.GameVersion}.");
        }
    }

    internal static void RequireVanillaInstallation(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null)
        {
            throw new InvalidOperationException("ICARUS environment metadata is unavailable.");
        }

        if (!installation.Metadata.TryGetValue(
                IcarusInstallationDiscovery.ModsRootPathKey,
                out var modsRoot) ||
            string.IsNullOrWhiteSpace(modsRoot))
        {
            throw new InvalidOperationException("ICARUS mods path is unavailable.");
        }

        var full = Path.GetFullPath(modsRoot);
        if (!Directory.Exists(full))
        {
            return;
        }

        try
        {
            if (!IcarusWorldDiscovery.IsRegularDirectory(full) ||
                Directory.EnumerateFileSystemEntries(full).Any())
            {
                throw new InvalidOperationException(
                    "ICARUS's active Paks mods directory is linked or non-empty. Steward's current ICARUS adapter is vanilla-only.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not safely inspect ICARUS's active Paks mods directory: {full}",
                ex);
        }
    }

    internal static string ReadRequiredBuildId(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                IcarusInstallationDiscovery.SteamManifestPathKey,
                out var manifestPath) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException(
                "ICARUS's Steam manifest is unavailable, so Steward cannot verify an exact game build.");
        }

        var full = Path.GetFullPath(manifestPath);
        FileStream stream;
        try
        {
            stream = new FileStream(
                full,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"ICARUS's Steam manifest could not be opened safely: {full}",
                ex);
        }

        using (stream)
        {
            var attributes = File.GetAttributes(full);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidOperationException(
                    $"ICARUS's Steam manifest is not a regular owned file: {full}");
            }

            if (stream.Length > MaximumSteamManifestBytes)
            {
                throw new InvalidOperationException(
                    $"ICARUS's Steam manifest exceeds Steward's {MaximumSteamManifestBytes}-byte metadata safety limit: {full}");
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
                    $"ICARUS's Steam manifest exceeded Steward's metadata safety limit while being read: {full}");
            }

            var match = SteamBuildIdRegex().Match(Encoding.UTF8.GetString(bytes, 0, total));
            if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                throw new InvalidOperationException(
                    $"Steam buildid was not found in ICARUS's manifest: {full}");
            }

            return match.Groups[1].Value;
        }
    }

    [GeneratedRegex("\\\"buildid\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamBuildIdRegex();
}
