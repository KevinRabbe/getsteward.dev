using System.Globalization;
using System.Net;
using System.Text.Json;

namespace SharedWorlds.Backend.Identity;

public enum ExternalIdentityTicketVerificationStatus
{
    Verified,
    InvalidTicket
}

public sealed record ExternalIdentityTicketVerificationResult(
    ExternalIdentityTicketVerificationStatus Status,
    VerifiedExternalIdentity? Identity);

public sealed record SteamWebApiTicketVerifierOptions
{
    public SteamWebApiTicketVerifierOptions(
        uint appId,
        string publisherApiKey,
        string identity)
    {
        if (appId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(appId), "Steam AppID is required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(publisherApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        AppId = appId;
        PublisherApiKey = publisherApiKey;
        Identity = identity;
    }

    public uint AppId { get; }
    public string PublisherApiKey { get; }
    public string Identity { get; }
}

public sealed class ExternalIdentityProviderException : Exception
{
    public ExternalIdentityProviderException(string provider, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        Provider = provider;
    }

    public ExternalIdentityProviderException(string provider, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        Provider = provider;
    }

    public string Provider { get; }
}

/// <summary>
/// Verifies a Steam GetAuthTicketForWebApi ticket from a secure backend and derives SteamID64 from
/// Steam's response. Publisher credentials never come from or return to the desktop client.
/// </summary>
public sealed class SteamWebApiTicketVerifier
{
    private const string Provider = "steam";
    private static readonly Uri Endpoint =
        new("https://partner.steam-api.com/ISteamUserAuth/AuthenticateUserTicket/v1/");

    private readonly HttpClient _httpClient;
    private readonly SteamWebApiTicketVerifierOptions _options;

    public SteamWebApiTicketVerifier(
        HttpClient httpClient,
        SteamWebApiTicketVerifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<ExternalIdentityTicketVerificationResult> VerifyAsync(
        string ticketHex,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidHexTicket(ticketHex))
        {
            return new(ExternalIdentityTicketVerificationStatus.InvalidTicket, null);
        }

        var requestUri = BuildRequestUri(ticketHex);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExternalIdentityProviderException(
                Provider,
                "Steam identity verification timed out.");
        }
        catch (HttpRequestException exception)
        {
            throw new ExternalIdentityProviderException(
                Provider,
                "Steam identity verification is unavailable.",
                exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new ExternalIdentityProviderException(
                    Provider,
                    $"Steam identity verification returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            }
            catch (JsonException exception)
            {
                throw new ExternalIdentityProviderException(
                    Provider,
                    "Steam identity verification returned an invalid response.",
                    exception);
            }

            using (document)
            {
                if (!TryReadResult(document.RootElement, out var result, out var steamId))
                {
                    throw new ExternalIdentityProviderException(
                        Provider,
                        "Steam identity verification returned an incomplete response.");
                }

                if (!string.Equals(result, "OK", StringComparison.Ordinal))
                {
                    return new(ExternalIdentityTicketVerificationStatus.InvalidTicket, null);
                }

                if (!ulong.TryParse(
                        steamId,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var steamId64) ||
                    steamId64 == 0)
                {
                    throw new ExternalIdentityProviderException(
                        Provider,
                        "Steam identity verification returned an invalid SteamID64.");
                }

                var identity = new VerifiedExternalIdentity(
                    new ExternalIdentityRef(
                        Provider,
                        steamId64.ToString(CultureInfo.InvariantCulture)));
                return new(ExternalIdentityTicketVerificationStatus.Verified, identity);
            }
        }
    }

    private Uri BuildRequestUri(string ticketHex)
    {
        var query = string.Join(
            '&',
            $"key={Uri.EscapeDataString(_options.PublisherApiKey)}",
            $"appid={_options.AppId.ToString(CultureInfo.InvariantCulture)}",
            $"ticket={Uri.EscapeDataString(ticketHex)}",
            $"identity={Uri.EscapeDataString(_options.Identity)}");
        return new UriBuilder(Endpoint) { Query = query }.Uri;
    }

    private static bool IsValidHexTicket(string ticketHex)
    {
        if (string.IsNullOrWhiteSpace(ticketHex) ||
            ticketHex.Length % 2 != 0 ||
            ticketHex.Length > 16_384)
        {
            return false;
        }

        foreach (var value in ticketHex)
        {
            if (!Uri.IsHexDigit(value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadResult(
        JsonElement root,
        out string? result,
        out string? steamId)
    {
        result = null;
        steamId = null;

        if (!root.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("result", out var resultElement))
        {
            return false;
        }

        result = resultElement.GetString();
        if (!string.Equals(result, "OK", StringComparison.Ordinal))
        {
            return result is not null;
        }

        if (!parameters.TryGetProperty("steamid", out var steamIdElement))
        {
            return false;
        }

        steamId = steamIdElement.GetString();
        return steamId is not null;
    }
}
