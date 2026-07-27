using System.Text;
using System.Text.RegularExpressions;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Smalland;

internal static partial class SmallandEnvironment
{
    internal const long MaximumSteamManifestBytes = 4L * 1024 * 1024;

    public static EnvironmentManifest Inspect(GameInstallation installation)
    {
        RequireVanillaInstallation(installation);
        return new EnvironmentManifest(
            1,
            "smalland",
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
                "smalland-environment-schema-unsupported",
                $"Smalland environment schema {requiredEnvironment.SchemaVersion} is not supported."));
        }

        if (!string.Equals(requiredEnvironment.AdapterId, "smalland", StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "smalland-adapter-mismatch",
                $"The required environment belongs to adapter '{requiredEnvironment.AdapterId}', not Smalland."));
        }

        if (requiredEnvironment.Components.Count != 0)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "smalland-components-unsupported",
                "The current Smalland adapter supports only vanilla Worlds."));
        }

        try
        {
            RequireVanillaInstallation(installation);
            var build = ReadRequiredBuildId(installation);
            if (!string.Equals(build, requiredEnvironment.GameVersion, StringComparison.Ordinal))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "smalland-version-mismatch",
                    $"This World requires Smalland Steam build {requiredEnvironment.GameVersion}, but this device has build {build}."));
            }
        }
        catch (InvalidOperationException ex)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "smalland-environment-unavailable",
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
            !string.Equals(requiredEnvironment.AdapterId, "smalland", StringComparison.Ordinal) ||
            requiredEnvironment.Components.Count != 0)
        {
            throw new InvalidOperationException(
                "The required environment is not a supported vanilla Smalland environment revision.");
        }

        RequireVanillaInstallation(installation);
        var installed = ReadRequiredBuildId(installation);
        if (!string.Equals(installed, requiredEnvironment.GameVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Smalland Steam build {installed} does not match required build {requiredEnvironment.GameVersion}.");
        }
    }

    internal static void RequireVanillaInstallation(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                SmallandInstallationDiscovery.PaksRootPathKey,
                out var paksRoot) ||
            string.IsNullOrWhiteSpace(paksRoot))
        {
            throw new InvalidOperationException("Smalland Paks path is unavailable.");
        }

        var full = Path.GetFullPath(paksRoot);
        if (!Directory.Exists(full))
        {
            return;
        }

        try
        {
            if (!SmallandWorldDiscovery.IsRegularDirectory(full))
            {
                throw new InvalidOperationException(
                    "Smalland's Paks directory is linked. Steward's current Smalland adapter is vanilla-only.");
            }

            foreach (var file in Directory.EnumerateFiles(
                         full,
                         "*.pak",
                         SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
                    !StockPakNameRegex().IsMatch(Path.GetFileName(file)))
                {
                    throw new InvalidOperationException(
                        "Smalland's active Paks directory contains a linked or non-stock-named .pak. Steward's current Smalland adapter is vanilla-only.");
                }
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not safely inspect Smalland's Paks directory: {full}",
                ex);
        }
    }

    internal static string ReadRequiredBuildId(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                SmallandInstallationDiscovery.SteamManifestPathKey,
                out var manifestPath) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException(
                "Smalland's Steam manifest is unavailable, so Steward cannot verify an exact game build.");
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
                $"Smalland's Steam manifest could not be opened safely: {full}",
                ex);
        }

        using (stream)
        {
            var attributes = File.GetAttributes(full);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidOperationException(
                    $"Smalland's Steam manifest is not a regular owned file: {full}");
            }

            if (stream.Length > MaximumSteamManifestBytes)
            {
                throw new InvalidOperationException(
                    $"Smalland's Steam manifest exceeds Steward's {MaximumSteamManifestBytes}-byte metadata safety limit: {full}");
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
                    $"Smalland's Steam manifest exceeded Steward's metadata safety limit while being read: {full}");
            }

            var match = SteamBuildIdRegex().Match(Encoding.UTF8.GetString(bytes, 0, total));
            if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                throw new InvalidOperationException(
                    $"Steam buildid was not found in Smalland's manifest: {full}");
            }

            return match.Groups[1].Value;
        }
    }

    [GeneratedRegex("^pakchunk[0-5]-WindowsNoEditor\\.pak$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StockPakNameRegex();

    [GeneratedRegex("\\\"buildid\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamBuildIdRegex();
}
