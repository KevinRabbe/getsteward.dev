using System.Globalization;
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
    private const int FriendsBuildConfigurationSchemaVersion = 1;
    private const int MaximumFriendsBuildConfigurationBytes = 4096;
    private const string ApiBaseAddressVariable = "STEWARD_API_BASE_URL";
    private const string AuthenticationModeVariable = "STEWARD_AUTH_MODE";
    private const string SteamAppIdVariable = "STEWARD_STEAM_APP_ID";
    private const string SteamIdentityVariable = "STEWARD_STEAM_WEB_API_IDENTITY";

    /// <summary>
    /// Remote sharing remains opt-in. Engineering/Steam deployments may use environment variables.
    /// A private Friends Build package instead carries one adjacent, non-secret JSON file containing
    /// only its HTTPS backend coordinate. Mixing both configuration sources is refused rather than
    /// selecting one implicitly.
    /// </summary>
    public static bool TryLoad(
        out StewardDesktopRemoteConfiguration? configuration,
        out string? problem)
        => TryLoad(
            Path.Combine(AppContext.BaseDirectory, FriendsBuildConfigurationFileName),
            out configuration,
            out problem);

    internal static bool TryLoad(
        string friendsBuildConfigurationPath,
        out StewardDesktopRemoteConfiguration? configuration,
        out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(friendsBuildConfigurationPath);

        var environmentConfigured = IsAnyEnvironmentConfigurationPresent();
        var packageConfigured = File.Exists(friendsBuildConfigurationPath);
        if (environmentConfigured && packageConfigured)
        {
            configuration = null;
            problem =
                $"Remote configuration is ambiguous: remove either {FriendsBuildConfigurationFileName} or the STEWARD_* environment configuration.";
            return false;
        }

        if (environmentConfigured)
        {
            return TryLoadFromEnvironment(out configuration, out problem);
        }

        if (packageConfigured)
        {
            return TryLoadFriendsBuildPackage(
                friendsBuildConfigurationPath,
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

        if (string.IsNullOrWhiteSpace(identity) ||
            identity.Any(char.IsWhiteSpace) ||
            identity.Length > 128)
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
                problem = $"{FriendsBuildConfigurationFileName} must be a regular file, not a linked file.";
                return false;
            }

            if (file.Length is <= 0 or > MaximumFriendsBuildConfigurationBytes)
            {
                problem = $"{FriendsBuildConfigurationFileName} has an invalid size.";
                return false;
            }

            var bytes = File.ReadAllBytes(file.FullName);
            if (bytes.Length is <= 0 or > MaximumFriendsBuildConfigurationBytes)
            {
                problem = $"{FriendsBuildConfigurationFileName} has an invalid size.";
                return false;
            }

            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                problem = $"{FriendsBuildConfigurationFileName} must contain one JSON object.";
                return false;
            }

            var schemaVersion = default(int?);
            string? apiText = null;
            foreach (var property in document.RootElement.EnumerateObject())
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

            if (!TryNormalizeFriendsBuildApiBaseAddress(apiText, out var apiBaseAddress))
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
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            NotSupportedException)
        {
            problem = $"{FriendsBuildConfigurationFileName} could not be read safely: {exception.Message}";
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

    private static bool TryNormalizeFriendsBuildApiBaseAddress(
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
