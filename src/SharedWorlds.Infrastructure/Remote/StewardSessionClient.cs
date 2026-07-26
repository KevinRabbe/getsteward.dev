using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SharedWorlds.Infrastructure.Remote;

public enum RemoteSteamAuthenticationStatus
{
    Authenticated,
    InvalidTicket
}

public enum RemoteFriendsBuildAuthenticationStatus
{
    Authenticated,
    InvalidCredential
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

public sealed record StewardRemoteAuthenticatedIdentity(
    string Provider,
    string ExternalId,
    string DisplayName);

public sealed record RemoteSteamAuthenticationResult(
    RemoteSteamAuthenticationStatus Status,
    StewardRemoteSessionTokens? Tokens = null);

public sealed record RemoteFriendsBuildAuthenticationResult(
    RemoteFriendsBuildAuthenticationStatus Status,
    StewardRemoteSessionTokens? Tokens = null,
    StewardRemoteAuthenticatedIdentity? Identity = null);

public sealed record RemoteSessionRefreshResult(
    RemoteSessionRefreshStatus Status,
    StewardRemoteSessionTokens? Tokens = null);

/// <summary>
/// Transport client for Steward authentication/session refresh. External proof acquisition remains a
/// platform/product-mode concern outside this client; successful Steam or private Friends Build proof
/// is exchanged for the same normal Steward access/refresh credentials.
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

    public async Task<RemoteFriendsBuildAuthenticationResult> AuthenticateFriendsBuildAsync(
        string credential,
        string installationId,
        CancellationToken cancellationToken = default)
    {
        ValidateCredential(credential, nameof(credential));
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/friends/session")
        {
            Content = JsonContent.Create(new FriendsBuildSessionRequest(credential, installationId))
        };
        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "Authenticated" => MapFriendsBuildAuthentication(response),
            "InvalidFriendsCredential" => new(RemoteFriendsBuildAuthenticationStatus.InvalidCredential),
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

    private RemoteFriendsBuildAuthenticationResult MapFriendsBuildAuthentication(ApiResponse response)
    {
        var data = DeserializeRequiredData<FriendsBuildSessionDataDto>(response);
        return new RemoteFriendsBuildAuthenticationResult(
            RemoteFriendsBuildAuthenticationStatus.Authenticated,
            data.Tokens.ToDomain(),
            data.Identity.ToDomain());
    }

    private async Task<ApiResponse> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await _apiClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var envelope = await RemoteApiJson.DeserializeAsync<ApiResponse>(
            response.Content,
            _jsonOptions,
            "session",
            cancellationToken);

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
            response.Retryable);

    private static void ValidateCredential(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Credential must be non-empty and contain no whitespace.", parameterName);
        }
    }

    private sealed record SteamSessionRequest(
        string TicketHex,
        string InstallationId);

    private sealed record FriendsBuildSessionRequest(
        string Credential,
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

    private sealed record FriendsBuildSessionDataDto(
        SessionTokensDto Tokens,
        AuthenticatedIdentityDto Identity);

    private sealed record AuthenticatedIdentityDto(
        string Provider,
        string ExternalId,
        string DisplayName)
    {
        public StewardRemoteAuthenticatedIdentity ToDomain()
        {
            if (string.IsNullOrWhiteSpace(Provider) ||
                Provider.Any(char.IsWhiteSpace) ||
                string.IsNullOrWhiteSpace(ExternalId) ||
                ExternalId.Any(char.IsWhiteSpace) ||
                string.IsNullOrWhiteSpace(DisplayName) ||
                DisplayName.Any(char.IsControl))
            {
                throw new InvalidDataException("Steward returned an invalid authenticated identity.");
            }

            return new StewardRemoteAuthenticatedIdentity(Provider, ExternalId, DisplayName);
        }
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
