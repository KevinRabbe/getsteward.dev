using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SharedWorlds.Core.Abstractions;
using SharedWorlds.Core.Environment;

namespace SharedWorlds.GameAdapters.SpaceEngineers;

internal static partial class SpaceEngineersEnvironment
{
    internal const long MaximumSteamManifestBytes = 4L * 1024 * 1024;
    internal const long MaximumWorldConfigBytes = 8L * 1024 * 1024;

    public static EnvironmentManifest Inspect(
        GameInstallation installation,
        DetectedWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        RequireVanillaWorld(world.SourcePath);
        return new EnvironmentManifest(
            1,
            "space-engineers",
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
                "space-engineers-environment-schema-unsupported",
                $"Space Engineers environment schema {requiredEnvironment.SchemaVersion} is not supported."));
        }

        if (!string.Equals(requiredEnvironment.AdapterId, "space-engineers", StringComparison.Ordinal))
        {
            issues.Add(new EnvironmentVerificationIssue(
                "space-engineers-adapter-mismatch",
                $"The required environment belongs to adapter '{requiredEnvironment.AdapterId}', not Space Engineers."));
        }

        if (requiredEnvironment.Components.Count != 0)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "space-engineers-components-unsupported",
                "The current Space Engineers adapter supports only vanilla Worlds."));
        }

        try
        {
            var build = ReadRequiredBuildId(installation);
            if (!string.Equals(build, requiredEnvironment.GameVersion, StringComparison.Ordinal))
            {
                issues.Add(new EnvironmentVerificationIssue(
                    "space-engineers-version-mismatch",
                    $"This World requires Space Engineers Steam build {requiredEnvironment.GameVersion}, but this device has build {build}."));
            }
        }
        catch (InvalidOperationException ex)
        {
            issues.Add(new EnvironmentVerificationIssue(
                "space-engineers-environment-unavailable",
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
            !string.Equals(requiredEnvironment.AdapterId, "space-engineers", StringComparison.Ordinal) ||
            requiredEnvironment.Components.Count != 0)
        {
            throw new InvalidOperationException(
                "The required environment is not a supported vanilla Space Engineers environment revision.");
        }

        var installed = ReadRequiredBuildId(installation);
        if (!string.Equals(installed, requiredEnvironment.GameVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Space Engineers Steam build {installed} does not match required build {requiredEnvironment.GameVersion}.");
        }
    }

    internal static void RequireVanillaWorld(string worldRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldRoot);
        var configPath = Path.Combine(
            Path.GetFullPath(worldRoot),
            SpaceEngineersWorldDiscovery.SandboxConfigFileName);
        if (!SpaceEngineersWorldDiscovery.IsRegularNonEmptyFile(configPath))
        {
            throw new InvalidOperationException(
                $"Space Engineers World configuration must be a regular non-empty file: {configPath}");
        }

        var length = new FileInfo(configPath).Length;
        if (length > MaximumWorldConfigBytes)
        {
            throw new InvalidOperationException(
                $"Space Engineers World configuration exceeds Steward's {MaximumWorldConfigBytes}-byte metadata safety limit: {configPath}");
        }

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumWorldConfigBytes
            };
            using var stream = new FileStream(
                configPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                useAsync: false);
            using var reader = XmlReader.Create(stream, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var mods = document
                .Descendants()
                .FirstOrDefault(element =>
                    string.Equals(element.Name.LocalName, "Mods", StringComparison.Ordinal));
            if (mods is null)
            {
                throw new InvalidOperationException(
                    "Space Engineers World configuration does not expose a bounded Mods list, so Steward cannot prove a vanilla environment.");
            }

            if (mods.Elements().Any())
            {
                throw new InvalidOperationException(
                    "Space Engineers World configuration enables mods. Steward's current Space Engineers adapter is vanilla-only.");
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            throw new InvalidOperationException(
                $"Steward could not safely inspect Space Engineers World mod configuration: {configPath}",
                ex);
        }
    }

    internal static string ReadRequiredBuildId(GameInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (installation.Metadata is null ||
            !installation.Metadata.TryGetValue(
                SpaceEngineersInstallationDiscovery.SteamManifestPathKey,
                out var manifestPath) ||
            string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new InvalidOperationException(
                "Space Engineers' Steam manifest is unavailable, so Steward cannot verify an exact game build.");
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
                useAsync: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Space Engineers' Steam manifest could not be opened safely: {full}",
                ex);
        }

        using (stream)
        {
            var attributes = File.GetAttributes(full);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new InvalidOperationException(
                    $"Space Engineers' Steam manifest is not a regular owned file: {full}");
            }

            if (stream.Length > MaximumSteamManifestBytes)
            {
                throw new InvalidOperationException(
                    $"Space Engineers' Steam manifest exceeds Steward's {MaximumSteamManifestBytes}-byte metadata safety limit: {full}");
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
                    $"Space Engineers' Steam manifest exceeded Steward's metadata safety limit while being read: {full}");
            }

            var match = SteamBuildIdRegex().Match(Encoding.UTF8.GetString(bytes, 0, total));
            if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                throw new InvalidOperationException(
                    $"Steam buildid was not found in Space Engineers' manifest: {full}");
            }

            return match.Groups[1].Value;
        }
    }

    [GeneratedRegex("\\\"buildid\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamBuildIdRegex();
}
