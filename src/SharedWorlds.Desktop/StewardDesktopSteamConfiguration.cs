using System.Globalization;
using System.IO;
using System.Text.Json;

namespace SharedWorlds.Desktop;

/// <summary>
/// Minimal process-local Steam platform configuration. Peer hosting needs only the expected Steam
/// AppID; HTTP endpoints, Web API identities, credentials, and remote authentication do not belong to
/// the Steam lifetime boundary.
/// </summary>
internal sealed record StewardDesktopSteamConfiguration(uint AppId)
{
    internal const string PackageConfigurationFileName = "steward-steam.json";
    internal const string SteamAppIdVariable = "STEWARD_STEAM_APP_ID";
    private const int SchemaVersion = 1;
    private const int MaximumConfigurationBytes = 1024;

    public static bool TryLoad(
        out StewardDesktopSteamConfiguration? configuration,
        out string? problem)
        => TryLoad(
            Path.Combine(AppContext.BaseDirectory, PackageConfigurationFileName),
            Environment.GetEnvironmentVariable(SteamAppIdVariable),
            out configuration,
            out problem);

    internal static bool TryLoad(
        string packageConfigurationPath,
        string? environmentAppId,
        out StewardDesktopSteamConfiguration? configuration,
        out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageConfigurationPath);
        configuration = null;

        var environmentConfigured = !string.IsNullOrWhiteSpace(environmentAppId);
        var packageConfigured = File.Exists(packageConfigurationPath);
        if (environmentConfigured && packageConfigured)
        {
            problem =
                $"Steam platform configuration is ambiguous: configure either {PackageConfigurationFileName} or {SteamAppIdVariable}, not both.";
            return false;
        }

        if (environmentConfigured)
        {
            if (!TryParsePositiveAppId(environmentAppId, out var environmentId))
            {
                problem = $"{SteamAppIdVariable} must be a positive Steam AppID.";
                return false;
            }

            configuration = new StewardDesktopSteamConfiguration(environmentId);
            problem = null;
            return true;
        }

        if (!packageConfigured)
        {
            problem = null;
            return false;
        }

        return TryLoadPackage(
            packageConfigurationPath,
            out configuration,
            out problem);
    }

    private static bool TryLoadPackage(
        string path,
        out StewardDesktopSteamConfiguration? configuration,
        out string? problem)
    {
        configuration = null;
        try
        {
            var file = new FileInfo(Path.GetFullPath(path));
            file.Refresh();
            if (!file.Exists)
            {
                problem = null;
                return false;
            }

            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                problem = $"{PackageConfigurationFileName} must be a regular file, not a linked file.";
                return false;
            }

            if (file.Length is <= 0 or > MaximumConfigurationBytes)
            {
                problem = $"{PackageConfigurationFileName} has an invalid size.";
                return false;
            }

            var bytes = File.ReadAllBytes(file.FullName);
            if (bytes.Length is <= 0 or > MaximumConfigurationBytes)
            {
                problem = $"{PackageConfigurationFileName} has an invalid size.";
                return false;
            }

            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 3
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                problem = $"{PackageConfigurationFileName} must contain one JSON object.";
                return false;
            }

            int? schemaVersion = null;
            uint? appId = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "schemaVersion":
                        if (schemaVersion is not null ||
                            property.Value.ValueKind != JsonValueKind.Number ||
                            !property.Value.TryGetInt32(out var parsedSchemaVersion))
                        {
                            problem = $"{PackageConfigurationFileName} contains an invalid schemaVersion.";
                            return false;
                        }

                        schemaVersion = parsedSchemaVersion;
                        break;
                    case "steamAppId":
                        if (appId is not null ||
                            property.Value.ValueKind != JsonValueKind.Number ||
                            !property.Value.TryGetUInt32(out var parsedAppId))
                        {
                            problem = $"{PackageConfigurationFileName} contains an invalid steamAppId.";
                            return false;
                        }

                        appId = parsedAppId;
                        break;
                    default:
                        problem = $"{PackageConfigurationFileName} contains unsupported field '{property.Name}'.";
                        return false;
                }
            }

            if (schemaVersion != SchemaVersion)
            {
                problem = $"{PackageConfigurationFileName} uses an unsupported schema version.";
                return false;
            }

            if (appId is null or 0)
            {
                problem = $"{PackageConfigurationFileName} must contain a positive steamAppId.";
                return false;
            }

            configuration = new StewardDesktopSteamConfiguration(appId.Value);
            problem = null;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            NotSupportedException)
        {
            problem = $"{PackageConfigurationFileName} could not be read safely: {exception.Message}";
            return false;
        }
    }

    private static bool TryParsePositiveAppId(
        string? text,
        out uint appId)
        => uint.TryParse(
               text,
               NumberStyles.None,
               CultureInfo.InvariantCulture,
               out appId) &&
           appId != 0;
}
