using System.Globalization;
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
    private const string ApiBaseAddressVariable = "STEWARD_API_BASE_URL";
    private const string AuthenticationModeVariable = "STEWARD_AUTH_MODE";
    private const string SteamAppIdVariable = "STEWARD_STEAM_APP_ID";
    private const string SteamIdentityVariable = "STEWARD_STEAM_WEB_API_IDENTITY";

    /// <summary>
    /// Remote sharing remains opt-in. Existing Steam deployments keep their previous configuration
    /// shape; the private Friends Build path requires an explicit auth mode so it can never become an
    /// accidental fallback when production Steam configuration is missing.
    /// </summary>
    public static bool TryLoadFromEnvironment(
        out StewardDesktopRemoteConfiguration? configuration,
        out string? problem)
    {
        var apiText = Environment.GetEnvironmentVariable(ApiBaseAddressVariable);
        var modeText = Environment.GetEnvironmentVariable(AuthenticationModeVariable);
        var appIdText = Environment.GetEnvironmentVariable(SteamAppIdVariable);
        var identity = Environment.GetEnvironmentVariable(SteamIdentityVariable);

        var anyConfigured = !string.IsNullOrWhiteSpace(apiText) ||
                            !string.IsNullOrWhiteSpace(modeText) ||
                            !string.IsNullOrWhiteSpace(appIdText) ||
                            !string.IsNullOrWhiteSpace(identity);
        if (!anyConfigured)
        {
            configuration = null;
            problem = null;
            return false;
        }

        if (!Uri.TryCreate(apiText, UriKind.Absolute, out var parsedApiBaseAddress) ||
            !StewardRemoteEndpointPolicy.TryNormalizeApiBaseAddress(
                parsedApiBaseAddress,
                out var apiBaseAddress) ||
            apiBaseAddress is null)
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
                apiBaseAddress,
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
            apiBaseAddress,
            StewardDesktopAuthenticationMode.Steam,
            steamAppId,
            identity);
        problem = null;
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
