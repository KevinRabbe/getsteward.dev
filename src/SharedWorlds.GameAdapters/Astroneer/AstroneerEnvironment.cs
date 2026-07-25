using System.Text;
using System.Text.RegularExpressions;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.Astroneer;

internal static partial class AstroneerEnvironment
{
    internal const long MaximumSteamManifestBytes = 4L * 1024 * 1024;

    public static EnvironmentManifest Inspect(GameInstallation installation)
    {
        RequireVanillaInstallation(installation);
        return new EnvironmentManifest(
            SchemaVersion: 1,
            AdapterId: "astroneer",
            GameVersion: ReadRequiredBuildId(installation),
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
                "astroneer-environment-schema-unsupported",
                $"ASTRONEER environment schema {requiredEnvironment.SchemaVersion} is not supported."));
        }

        if (!string.Equals(requiredEnvironment.AdapterId, "astroneer", StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "astroneer-adapter-mismatch",
                $"The required environment belongs to adapter '{requiredEnvironment.AdapterId}', not ASTRONEER."));
        }

        if (requiredEnvironment.Components.Count != 0)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "astroneer-components-unsupported",
                "The current ASTRONEER adapter supports only vanilla Worlds and no adapter-managed mod components."));
        }

        try
        {
            RequireVanillaInstallation(installation);
            var installedBuild = ReadRequiredBuildId(installation);
            if (!string.Equals(installedBuild, requiredEnvironment.GameVersion, StringComparison.Ordinal))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "astroneer-version-mismatch",
                    $"This World requires ASTRONEER Steam build {requiredEnvironment.GameVersion}, but this device has build {installedBuild}."));
            }
        }
        catch (InvalidOperationException exception)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "astroneer-environment-unavailable",
                exception.Message));
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
            !string.Equals(requiredEnvironment.AdapterId, "astroneer", StringComparison.Ordinal) ||
            requiredEnvironment.Components.Count != 0)
        {
            throw new InvalidOperationException(
                "The required environment is not a supported vanilla ASTRONEER environment revision.");
        }

        RequireVanillaInstallation(installation);
        var installedBuild = ReadRequiredBuildId(installation);
        if (!string.Equals(installedBuild, requiredEnvironment.GameVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ASTRONEER Steam build {installedBuild} does not match required build {requiredEnvironment.GameVersion}.");
        }
    }

    internal static void RequireVanillaInstallation(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null)
        {
            throw new InvalidOperationException("ASTRONEER environment metadata is unavailable.");
        }

        RequireEmptyOptionalDirectory(
            installation.Metadata,
            AstroneerInstallationDiscovery.ModsRootPathKey,
            "ASTRONEER's Saved/Mods directory");
        RequireEmptyOptionalDirectory(
            installation.Metadata,
            AstroneerInstallationDiscovery.PaksRootPathKey,
            "ASTRONEER's Saved/Paks directory");
    }

    private static void RequireEmptyOptionalDirectory(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        string description)
    {
        if (!metadata.TryGetValue(key, out var path) || string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"{description} path is unavailable.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            return;
        }

        try
        {
            if (!AstroneerWorldDiscovery.IsRegularDirectory(fullPath) ||
                Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                throw new InvalidOperationException(
                    $"{description} is linked or non-empty. Steward's current ASTRONEER adapter is vanilla-only.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Steward could not safely inspect {description}: {fullPath}",
                exception);
        }
    }

    internal static string ReadRequiredBuildId(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                AstroneerInstallationDiscovery.SteamManifestPathKey,
                out var manifestPath) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException(
                "ASTRONEER's Steam manifest is unavailable, so Steward cannot verify an exact game build.");
        }

        var fullPath = Path.GetFullPath(manifestPath);
        using var stream = OpenOwnedManifest(fullPath);
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
                $"ASTRONEER's Steam manifest exceeded Steward's {MaximumSteamManifestBytes}-byte metadata safety limit while being read: {fullPath}");
        }

        var text = Encoding.UTF8.GetString(bytes, 0, total);
        var match = SteamBuildIdRegex().Match(text);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
        {
            throw new InvalidOperationException(
                $"Steam buildid was not found in ASTRONEER's manifest: {fullPath}");
        }

        return match.Groups[1].Value;
    }

    private static FileStream OpenOwnedManifest(string fullPath)
    {
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"ASTRONEER's Steam manifest could not be opened safely: {fullPath}",
                exception);
        }

        try
        {
            var attributes = File.GetAttributes(fullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                (attributes & FileAttributes.Directory) != 0)
            {
                throw new InvalidOperationException(
                    $"ASTRONEER's Steam manifest is not a regular owned file: {fullPath}");
            }

            if (stream.Length > MaximumSteamManifestBytes)
            {
                throw new InvalidOperationException(
                    $"ASTRONEER's Steam manifest exceeds Steward's {MaximumSteamManifestBytes}-byte metadata safety limit: {fullPath}");
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    [GeneratedRegex("\\\"buildid\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamBuildIdRegex();
}
