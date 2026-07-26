using System.Text;
using System.Text.RegularExpressions;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.AbioticFactor;

internal static partial class AbioticFactorEnvironment
{
    internal const long MaximumSteamManifestBytes = 4L * 1024 * 1024;

    public static EnvironmentManifest Inspect(GameInstallation installation)
    {
        RequireVanillaInstallation(installation);
        return new EnvironmentManifest(
            1,
            "abiotic-factor",
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
                "abiotic-factor-environment-schema-unsupported",
                $"Abiotic Factor environment schema {requiredEnvironment.SchemaVersion} is not supported."));
        }

        if (!string.Equals(requiredEnvironment.AdapterId, "abiotic-factor", StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "abiotic-factor-adapter-mismatch",
                $"The required environment belongs to adapter '{requiredEnvironment.AdapterId}', not Abiotic Factor."));
        }

        if (requiredEnvironment.Components.Count != 0)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "abiotic-factor-components-unsupported",
                "The current Abiotic Factor adapter supports only vanilla Worlds."));
        }

        try
        {
            RequireVanillaInstallation(installation);
            var build = ReadRequiredBuildId(installation);
            if (!string.Equals(build, requiredEnvironment.GameVersion, StringComparison.Ordinal))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "abiotic-factor-version-mismatch",
                    $"This World requires Abiotic Factor Steam build {requiredEnvironment.GameVersion}, but this device has build {build}."));
            }
        }
        catch (InvalidOperationException ex)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "abiotic-factor-environment-unavailable",
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
            !string.Equals(requiredEnvironment.AdapterId, "abiotic-factor", StringComparison.Ordinal) ||
            requiredEnvironment.Components.Count != 0)
        {
            throw new InvalidOperationException(
                "The required environment is not a supported vanilla Abiotic Factor environment revision.");
        }

        RequireVanillaInstallation(installation);
        var installed = ReadRequiredBuildId(installation);
        if (!string.Equals(installed, requiredEnvironment.GameVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Abiotic Factor Steam build {installed} does not match required build {requiredEnvironment.GameVersion}.");
        }
    }

    internal static void RequireVanillaInstallation(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                AbioticFactorInstallationDiscovery.Ue4SsRootPathKey,
                out var loaderRoot) ||
            string.IsNullOrWhiteSpace(loaderRoot))
        {
            throw new InvalidOperationException("Abiotic Factor's UE4SS loader path is unavailable.");
        }

        var root = Path.GetFullPath(loaderRoot);
        var proxyDll = Path.Combine(root, "dwmapi.dll");
        var ue4ssDirectory = Path.Combine(root, "ue4ss");

        try
        {
            if (File.Exists(proxyDll))
            {
                var attributes = File.GetAttributes(proxyDll);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new InvalidOperationException(
                        "Abiotic Factor's UE4SS proxy marker is linked or non-regular. Steward's current Abiotic Factor adapter is vanilla-only.");
                }

                throw new InvalidOperationException(
                    "Abiotic Factor's UE4SS proxy marker is present. Steward's current Abiotic Factor adapter is vanilla-only.");
            }

            if (Directory.Exists(ue4ssDirectory))
            {
                if (!AbioticFactorWorldDiscovery.IsRegularDirectory(ue4ssDirectory))
                {
                    throw new InvalidOperationException(
                        "Abiotic Factor's UE4SS directory is linked. Steward's current Abiotic Factor adapter is vanilla-only.");
                }

                throw new InvalidOperationException(
                    "Abiotic Factor's UE4SS directory is present. Steward's current Abiotic Factor adapter is vanilla-only.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not safely inspect Abiotic Factor's UE4SS loader surface: {root}",
                ex);
        }
    }

    internal static string ReadRequiredBuildId(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                AbioticFactorInstallationDiscovery.SteamManifestPathKey,
                out var manifestPath) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException(
                "Abiotic Factor's Steam manifest is unavailable, so Steward cannot verify an exact game build.");
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
                $"Abiotic Factor's Steam manifest could not be opened safely: {full}",
                ex);
        }

        using (stream)
        {
            var attributes = File.GetAttributes(full);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidOperationException(
                    $"Abiotic Factor's Steam manifest is not a regular owned file: {full}");
            }

            if (stream.Length > MaximumSteamManifestBytes)
            {
                throw new InvalidOperationException(
                    $"Abiotic Factor's Steam manifest exceeds Steward's {MaximumSteamManifestBytes}-byte metadata safety limit: {full}");
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
                    $"Abiotic Factor's Steam manifest exceeded Steward's metadata safety limit while being read: {full}");
            }

            var match = SteamBuildIdRegex().Match(Encoding.UTF8.GetString(bytes, 0, total));
            if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                throw new InvalidOperationException(
                    $"Steam buildid was not found in Abiotic Factor's manifest: {full}");
            }

            return match.Groups[1].Value;
        }
    }

    [GeneratedRegex("\\\"buildid\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamBuildIdRegex();
}
