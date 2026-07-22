using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SharedWorlds.Infrastructure.Remote;

public enum RemoteSteamAuthenticationStatus
{
    Authenticated,
    InvalidTicket
}

public enum RemoteSessionRefreshStatus
{
    Refreshed,
    InvalidCredential
}

public sealed record StewardRemoteSessionTokens(
    string AccessToken,
    DateTimeOffset AccessExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt);

public sealed record RemoteSteamAuthenticationResult(
    RemoteSteamAuthenticationStatus Status,
    StewardRemoteSessionTokens? Tokens = null);

public sealed record RemoteSessionRefreshResult(
    RemoteSessionRefreshStatus Status,
    StewardRemoteSessionTokens? Tokens = null);

/// <summary>
/// Transport client for Steward authentication/session refresh. Steam ticket acquisition remains a
/// platform concern outside this client; the client only exchanges an already-issued ticket for
/// Steward credentials and rotates them through the backend contract.
/// </summary>
public sealed class StewardSessionClient
{
    private readonly HttpClient _apiClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public StewardSessionClient(HttpClient apiClient)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        if (apiClient.BaseAddress is null)
        {
            throw new ArgumentException("Steward API HttpClient requires a BaseAddress.", nameof(apiClient));
        }

        _apiClient = apiClient;
    }

    public async Task<RemoteSteamAuthenticationResult> AuthenticateSteamAsync(
        string ticketHex,
        string installationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketHex);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/steam/session")
        {
            Content = JsonContent.Create(new SteamSessionRequest(ticketHex, installationId))
        };
        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "Authenticated" => new(
                RemoteSteamAuthenticationStatus.Authenticated,
                DeserializeRequiredData<SessionTokensDto>(response).ToDomain()),
            "InvalidSteamTicket" => new(RemoteSteamAuthenticationStatus.InvalidTicket),
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    public async Task<RemoteSessionRefreshResult> RefreshAsync(
        string refreshToken,
        string installationId,
        CancellationToken cancellationToken = default)
    {
        ValidateCredential(refreshToken, nameof(refreshToken));
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/refresh")
        {
            Content = JsonContent.Create(new RefreshRequest(refreshToken, installationId))
        };
        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "Refreshed" => new(
                RemoteSessionRefreshStatus.Refreshed,
                DeserializeRequiredData<SessionTokensDto>(response).ToDomain()),
            "InvalidRefreshCredential" => new(RemoteSessionRefreshStatus.InvalidCredential),
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    public async Task<bool> RevokeAsync(
        string refreshToken,
        string installationId,
        CancellationToken cancellationToken = default)
    {
        ValidateCredential(refreshToken, nameof(refreshToken));
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/revoke")
        {
            Content = JsonContent.Create(new RevokeRequest(refreshToken, installationId))
        };
        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "Revoked" => true,
            "NotRevoked" => false,
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    private async Task<ApiResponse> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await _apiClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        ApiResponse? envelope;
        try
        {
            envelope = await JsonSerializer.DeserializeAsync<ApiResponse>(stream, _jsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Steward returned malformed session JSON.", exception);
        }

        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Code))
        {
            throw new InvalidDataException("Steward returned an invalid session response.");
        }

        return envelope with { StatusCode = response.StatusCode };
    }

    private T DeserializeRequiredData<T>(ApiResponse response)
    {
        if (response.Data is null || response.Data.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidDataException($"Steward response '{response.Code}' omitted required data.");
        }

        try
        {
            return response.Data.Value.Deserialize<T>(_jsonOptions)
                   ?? throw new InvalidDataException(
                       $"Steward response '{response.Code}' returned null required data.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Steward returned invalid session data for '{response.Code}'.",
                exception);
        }
    }

    private static StewardRemoteApiException CreateUnexpectedResponse(ApiResponse response)
        => new(
            response.StatusCode,
            response.Code,
            response.Retryable || IsTransientStatus(response.StatusCode));

    private static void ValidateCredential(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Credential must be non-empty and contain no whitespace.", parameterName);
        }
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.RequestTimeout ||
           statusCode == HttpStatusCode.TooManyRequests ||
           (int)statusCode >= 500;

    private sealed record SteamSessionRequest(
        string TicketHex,
        string InstallationId);

    private sealed record RefreshRequest(
        string RefreshToken,
        string InstallationId);

    private sealed record RevokeRequest(
        string RefreshToken,
        string InstallationId);

    private sealed record ApiResponse(
        string Code,
        JsonElement? Data,
        bool Retryable)
    {
        public HttpStatusCode StatusCode { get; init; }
    }

    private sealed record SessionTokensDto(
        string AccessToken,
        DateTimeOffset AccessExpiresAt,
        string RefreshToken,
        DateTimeOffset RefreshExpiresAt)
    {
        public StewardRemoteSessionTokens ToDomain()
            => new(AccessToken, AccessExpiresAt, RefreshToken, RefreshExpiresAt);
    }
}
