using System.Globalization;
using System.IO;
using System.Text.Json;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Desktop;

internal enum StewardDesktopAuthenticationMode
{
    Steam,
    FriendsBuild
}

internal sealed record StewardDesktopRemoteConfiguration(
    Uri ApiBaseAddress,
    StewardDesktopAuthenticationMode AuthenticationMode,
    uint? SteamAppId,
    string? SteamWebApiIdentity)
{
    internal const string FriendsBuildConfigurationFileName = "steward-friends-build.json";
    internal const string SteamReleaseConfigurationFileName = "steward-steam-release.json";
    private const int FriendsBuildConfigurationSchemaVersion = 1;
    private const int SteamReleaseConfigurationSchemaVersion = 1;
    private const int MaximumPackageConfigurationBytes = 4096;
    private const string ApiBaseAddressVariable = "STEWARD_API_BASE_URL";
    private const string AuthenticationModeVariable = "STEWARD_AUTH_MODE";
    private const string SteamAppIdVariable = "STEWARD_STEAM_APP_ID";
    private const string SteamIdentityVariable = "STEWARD_STEAM_WEB_API_IDENTITY";

    /// <summary>
    /// Remote sharing remains opt-in. Engineering deployments may use environment variables.
    /// Private Friends Build and commercial Steam release packages instead carry adjacent,
    /// non-secret configuration files. Multiple configuration sources are refused rather than
    /// selecting one implicitly.
    /// </summary>
    public static bool TryLoad(
        out StewardDesktopRemoteConfiguration? configuration,
        out string? problem)
        => TryLoad(
            Path.Combine(AppContext.BaseDirectory, FriendsBuildConfigurationFileName),
            Path.Combine(AppContext.BaseDirectory, SteamReleaseConfigurationFileName),
            out configuration,
            out problem);

    internal static bool TryLoad(
        string friendsBuildConfigurationPath,
        out StewardDesktopRemoteConfiguration? configuration,
        out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(friendsBuildConfigurationPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(friendsBuildConfigurationPath))
            ?? throw new InvalidOperationException("Remote configuration path has no containing directory.");
        return TryLoad(
            friendsBuildConfigurationPath,
            Path.Combine(directory, SteamReleaseConfigurationFileName),
            out configuration,
            out problem);
    }

    internal static bool TryLoad(
        string friendsBuildConfigurationPath,
        string steamReleaseConfigurationPath,
        out StewardDesktopRemoteConfiguration? configuration,
        out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(friendsBuildConfigurationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(steamReleaseConfigurationPath);

        var environmentConfigured = IsAnyEnvironmentConfigurationPresent();
        var friendsPackageConfigured = File.Exists(friendsBuildConfigurationPath);
        var steamPackageConfigured = File.Exists(steamReleaseConfigurationPath);
        var configuredSourceCount =
            (environmentConfigured ? 1 : 0) +
            (friendsPackageConfigured ? 1 : 0) +
            (steamPackageConfigured ? 1 : 0);
        if (configuredSourceCount > 1)
        {
            configuration = null;
            problem =
                $"Remote configuration is ambiguous: configure exactly one of {FriendsBuildConfigurationFileName}, {SteamReleaseConfigurationFileName}, or the STEWARD_* engineering environment configuration.";
            return false;
        }

        if (environmentConfigured)
        {
            return TryLoadFromEnvironment(out configuration, out problem);
        }

        if (friendsPackageConfigured)
        {
            return TryLoadFriendsBuildPackage(
                friendsBuildConfigurationPath,
                out configuration,
                out problem);
        }

        if (steamPackageConfigured)
        {
            return TryLoadSteamReleasePackage(
                steamReleaseConfigurationPath,
                out configuration,
                out problem);
        }

        configuration = null;
        problem = null;
        return false;
    }

    internal static bool TryLoadFromEnvironment(
        out StewardDesktopRemoteConfiguration? configuration,
        out string? problem)
    {
        var apiText = Environment.GetEnvironmentVariable(ApiBaseAddressVariable);
        var modeText = Environment.GetEnvironmentVariable(AuthenticationModeVariable);
        var appIdText = Environment.GetEnvironmentVariable(SteamAppIdVariable);
        var identity = Environment.GetEnvironmentVariable(SteamIdentityVariable);

        if (!TryNormalizeEnvironmentApiBaseAddress(apiText, out var apiBaseAddress))
        {
            configuration = null;
            problem = $"{ApiBaseAddressVariable} must use HTTPS. Plain HTTP is allowed only for a loopback development endpoint.";
            return false;
        }

        var mode = ParseMode(modeText, appIdText, identity);
        if (mode is null)
        {
            configuration = null;
            problem = $"{AuthenticationModeVariable} must be 'steam' or 'friends-build'. Existing complete Steam configuration may omit it.";
            return false;
        }

        if (mode == StewardDesktopAuthenticationMode.FriendsBuild)
        {
            if (!string.IsNullOrWhiteSpace(appIdText) || !string.IsNullOrWhiteSpace(identity))
            {
                configuration = null;
                problem = $"{SteamAppIdVariable} and {SteamIdentityVariable} must be omitted in Friends Build mode.";
                return false;
            }

            configuration = new StewardDesktopRemoteConfiguration(
                apiBaseAddress!,
                StewardDesktopAuthenticationMode.FriendsBuild,
                SteamAppId: null,
                SteamWebApiIdentity: null);
            problem = null;
            return true;
        }

        if (!uint.TryParse(
                appIdText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var steamAppId) ||
            steamAppId == 0)
        {
            configuration = null;
            problem = $"{SteamAppIdVariable} must be a positive Steam AppID.";
            return false;
        }

        if (!IsValidSteamWebApiIdentity(identity))
        {
            configuration = null;
            problem = $"{SteamIdentityVariable} must be a non-empty service identity without whitespace.";
            return false;
        }

        configuration = new StewardDesktopRemoteConfiguration(
            apiBaseAddress!,
            StewardDesktopAuthenticationMode.Steam,
            steamAppId,
            identity);
        problem = null;
        return true;
    }

    internal static bool TryLoadFriendsBuildPackage(
        string path,
        out StewardDesktopRemoteConfiguration? configuration,
        out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        configuration = null;

        if (!TryReadPackageConfiguration(
                path,
                FriendsBuildConfigurationFileName,
                out var document,
                out problem))
        {
            return false;
        }

        using (document)
        {
            var schemaVersion = default(int?);
            string? apiText = null;
            foreach (var property in document!.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "schemaVersion":
                        if (schemaVersion is not null ||
                            property.Value.ValueKind != JsonValueKind.Number ||
                            !property.Value.TryGetInt32(out var parsedVersion))
                        {
                            problem = $"{FriendsBuildConfigurationFileName} contains an invalid schemaVersion.";
                            return false;
                        }

                        schemaVersion = parsedVersion;
                        break;
                    case "apiBaseUrl":
                        if (apiText is not null || property.Value.ValueKind != JsonValueKind.String)
                        {
                            problem = $"{FriendsBuildConfigurationFileName} contains an invalid apiBaseUrl.";
                            return false;
                        }

                        apiText = property.Value.GetString();
                        break;
                    default:
                        problem = $"{FriendsBuildConfigurationFileName} contains unsupported field '{property.Name}'.";
                        return false;
                }
            }

            if (schemaVersion != FriendsBuildConfigurationSchemaVersion)
            {
                problem = $"{FriendsBuildConfigurationFileName} uses an unsupported schema version.";
                return false;
            }

            if (!TryNormalizePackageApiBaseAddress(apiText, out var apiBaseAddress))
            {
                problem = $"{FriendsBuildConfigurationFileName} must contain an HTTPS apiBaseUrl without credentials, query, or fragment.";
                return false;
            }

            configuration = new StewardDesktopRemoteConfiguration(
                apiBaseAddress!,
                StewardDesktopAuthenticationMode.FriendsBuild,
                SteamAppId: null,
                SteamWebApiIdentity: null);
            problem = null;
            return true;
        }
    }

    internal static bool TryLoadSteamReleasePackage(
        string path,
        out StewardDesktopRemoteConfiguration? configuration,
        out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        configuration = null;

        if (!TryReadPackageConfiguration(
                path,
                SteamReleaseConfigurationFileName,
                out var document,
                out problem))
        {
            return false;
        }

        using (document)
        {
            var schemaVersion = default(int?);
            string? apiText = null;
            uint? steamAppId = null;
            string? steamWebApiIdentity = null;
            foreach (var property in document!.RootElement.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "schemaVersion":
                        if (schemaVersion is not null ||
                            property.Value.ValueKind != JsonValueKind.Number ||
                            !property.Value.TryGetInt32(out var parsedVersion))
                        {
                            problem = $"{SteamReleaseConfigurationFileName} contains an invalid schemaVersion.";
                            return false;
                        }

                        schemaVersion = parsedVersion;
                        break;
                    case "apiBaseUrl":
                        if (apiText is not null || property.Value.ValueKind != JsonValueKind.String)
                        {
                            problem = $"{SteamReleaseConfigurationFileName} contains an invalid apiBaseUrl.";
                            return false;
                        }

                        apiText = property.Value.GetString();
                        break;
                    case "steamAppId":
                        if (steamAppId is not null ||
                            property.Value.ValueKind != JsonValueKind.Number ||
                            !property.Value.TryGetUInt32(out var parsedAppId))
                        {
                            problem = $"{SteamReleaseConfigurationFileName} contains an invalid steamAppId.";
                            return false;
                        }

                        steamAppId = parsedAppId;
                        break;
                    case "steamWebApiIdentity":
                        if (steamWebApiIdentity is not null || property.Value.ValueKind != JsonValueKind.String)
                        {
                            problem = $"{SteamReleaseConfigurationFileName} contains an invalid steamWebApiIdentity.";
                            return false;
                        }

                        steamWebApiIdentity = property.Value.GetString();
                        break;
                    default:
                        problem = $"{SteamReleaseConfigurationFileName} contains unsupported field '{property.Name}'.";
                        return false;
                }
            }

            if (schemaVersion != SteamReleaseConfigurationSchemaVersion)
            {
                problem = $"{SteamReleaseConfigurationFileName} uses an unsupported schema version.";
                return false;
            }

            if (!TryNormalizePackageApiBaseAddress(apiText, out var apiBaseAddress))
            {
                problem = $"{SteamReleaseConfigurationFileName} must contain an HTTPS apiBaseUrl without credentials, query, or fragment.";
                return false;
            }

            if (steamAppId is null or 0)
            {
                problem = $"{SteamReleaseConfigurationFileName} must contain a positive steamAppId.";
                return false;
            }

            if (!IsValidSteamWebApiIdentity(steamWebApiIdentity))
            {
                problem = $"{SteamReleaseConfigurationFileName} must contain a non-empty steamWebApiIdentity without whitespace.";
                return false;
            }

            configuration = new StewardDesktopRemoteConfiguration(
                apiBaseAddress!,
                StewardDesktopAuthenticationMode.Steam,
                steamAppId.Value,
                steamWebApiIdentity);
            problem = null;
            return true;
        }
    }

    private static bool TryReadPackageConfiguration(
        string path,
        string configurationFileName,
        out JsonDocument? document,
        out string? problem)
    {
        document = null;
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
                problem = $"{configurationFileName} must be a regular file, not a linked file.";
                return false;
            }

            if (file.Length is <= 0 or > MaximumPackageConfigurationBytes)
            {
                problem = $"{configurationFileName} has an invalid size.";
                return false;
            }

            var bytes = File.ReadAllBytes(file.FullName);
            if (bytes.Length is <= 0 or > MaximumPackageConfigurationBytes)
            {
                problem = $"{configurationFileName} has an invalid size.";
                return false;
            }

            var parsed = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4
                });
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            {
                parsed.Dispose();
                problem = $"{configurationFileName} must contain one JSON object.";
                return false;
            }

            document = parsed;
            problem = null;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            NotSupportedException)
        {
            document?.Dispose();
            document = null;
            problem = $"{configurationFileName} could not be read safely: {exception.Message}";
            return false;
        }
    }

    private static bool IsAnyEnvironmentConfigurationPresent()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiBaseAddressVariable)) ||
           !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AuthenticationModeVariable)) ||
           !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SteamAppIdVariable)) ||
           !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SteamIdentityVariable));

    private static bool TryNormalizeEnvironmentApiBaseAddress(
        string? apiText,
        out Uri? apiBaseAddress)
    {
        apiBaseAddress = null;
        return Uri.TryCreate(apiText, UriKind.Absolute, out var parsedApiBaseAddress) &&
               StewardRemoteEndpointPolicy.TryNormalizeApiBaseAddress(
                   parsedApiBaseAddress,
                   out apiBaseAddress) &&
               apiBaseAddress is not null;
    }

    private static bool TryNormalizePackageApiBaseAddress(
        string? apiText,
        out Uri? apiBaseAddress)
    {
        apiBaseAddress = null;
        if (!Uri.TryCreate(apiText, UriKind.Absolute, out var parsedApiBaseAddress) ||
            !string.Equals(parsedApiBaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(parsedApiBaseAddress.UserInfo) ||
            !string.IsNullOrEmpty(parsedApiBaseAddress.Query) ||
            !string.IsNullOrEmpty(parsedApiBaseAddress.Fragment) ||
            !StewardRemoteEndpointPolicy.TryNormalizeApiBaseAddress(
                parsedApiBaseAddress,
                out apiBaseAddress) ||
            apiBaseAddress is null)
        {
            apiBaseAddress = null;
            return false;
        }

        return true;
    }

    private static bool IsValidSteamWebApiIdentity(string? identity)
        => !string.IsNullOrWhiteSpace(identity) &&
           identity.Length <= 128 &&
           !identity.Any(char.IsWhiteSpace);

    private static StewardDesktopAuthenticationMode? ParseMode(
        string? modeText,
        string? appIdText,
        string? identity)
    {
        if (string.IsNullOrWhiteSpace(modeText))
        {
            return !string.IsNullOrWhiteSpace(appIdText) && !string.IsNullOrWhiteSpace(identity)
                ? StewardDesktopAuthenticationMode.Steam
                : null;
        }

        if (string.Equals(modeText, "steam", StringComparison.OrdinalIgnoreCase))
        {
            return StewardDesktopAuthenticationMode.Steam;
        }

        if (string.Equals(modeText, "friends-build", StringComparison.OrdinalIgnoreCase))
        {
            return StewardDesktopAuthenticationMode.FriendsBuild;
        }

        return null;
    }
}
