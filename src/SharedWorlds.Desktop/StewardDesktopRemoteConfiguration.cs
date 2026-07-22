using System.Globalization;

namespace SharedWorlds.Desktop;

internal sealed record StewardDesktopRemoteConfiguration(
    Uri ApiBaseAddress,
    uint SteamAppId,
    string SteamWebApiIdentity)
{
    private const string ApiBaseAddressVariable = "STEWARD_API_BASE_URL";
    private const string SteamAppIdVariable = "STEWARD_STEAM_APP_ID";
    private const string SteamIdentityVariable = "STEWARD_STEAM_WEB_API_IDENTITY";

    /// <summary>
    /// Remote sharing is opt-in until Steward has its production Steam/App deployment values. With no
    /// remote variables present the desktop remains a fully functional local-only application. A
    /// partial configuration is treated as invalid rather than guessing any identity or AppID.
    /// </summary>
    public static bool TryLoadFromEnvironment(
        out StewardDesktopRemoteConfiguration? configuration,
        out string? problem)
    {
        var apiText = Environment.GetEnvironmentVariable(ApiBaseAddressVariable);
        var appIdText = Environment.GetEnvironmentVariable(SteamAppIdVariable);
        var identity = Environment.GetEnvironmentVariable(SteamIdentityVariable);

        var anyConfigured = !string.IsNullOrWhiteSpace(apiText) ||
                            !string.IsNullOrWhiteSpace(appIdText) ||
                            !string.IsNullOrWhiteSpace(identity);
        if (!anyConfigured)
        {
            configuration = null;
            problem = null;
            return false;
        }

        if (!Uri.TryCreate(apiText, UriKind.Absolute, out var apiBaseAddress) ||
            apiBaseAddress.Scheme is not ("http" or "https"))
        {
            configuration = null;
            problem = $"{ApiBaseAddressVariable} must be an absolute HTTP or HTTPS URI.";
            return false;
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
            steamAppId,
            identity);
        problem = null;
        return true;
    }
}
