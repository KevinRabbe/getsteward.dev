using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Remote;

public sealed record StewardRemoteWorldPlayerPresence(
    string Provider,
    string ExternalId);

public sealed record StewardRemoteLobbyHost(
    string Provider,
    string ExternalId,
    StewardRemoteHostPresenceState State);

public sealed record StewardRemoteWorldPlayerPresenceSnapshot(
    IReadOnlyList<StewardRemoteWorldPlayerPresence> Players,
    StewardRemoteLobbyHost? Host);

/// <summary>
/// Client for the deliberately non-authoritative World-lobby snapshot. Player presence is short-lived
/// presentation evidence; Host identity comes from the backend's existing reservation-backed Host
/// presence. Neither can grant access or mutate World authority.
/// </summary>
public sealed class StewardWorldPlayerPresenceClient
{
    private readonly HttpClient _apiClient;
    private readonly IStewardAccessTokenProvider _accessTokens;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public StewardWorldPlayerPresenceClient(
        HttpClient apiClient,
        IStewardAccessTokenProvider accessTokens)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(accessTokens);
        if (apiClient.BaseAddress is null)
        {
            throw new ArgumentException("Steward API HttpClient requires a BaseAddress.", nameof(apiClient));
        }

        _apiClient = apiClient;
        _accessTokens = accessTokens;
    }

    public async Task<StewardRemoteWorldPlayerPresenceSnapshot> GetSnapshotAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Get,
            $"api/v1/worlds/{worldId.Value:D}/player-presence",
            cancellationToken);
        var response = await SendAsync(request, cancellationToken);
        if (!string.Equals(response.Code, "PlayerPresence", StringComparison.Ordinal))
        {
            throw CreateUnexpectedResponse(response);
        }

        var data = DeserializeRequiredData<PlayerPresenceSnapshotDto>(response);
        return new StewardRemoteWorldPlayerPresenceSnapshot(
            data.Players
                .Select(static item => new StewardRemoteWorldPlayerPresence(item.Provider, item.ExternalId))
                .ToArray(),
            data.Host?.ToDomain());
    }

    public async Task PublishAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Put,
            $"api/v1/worlds/{worldId.Value:D}/player-presence",
            cancellationToken);
        var response = await SendAsync(request, cancellationToken);
        if (!string.Equals(response.Code, "PlayerPresencePublished", StringComparison.Ordinal))
        {
            throw CreateUnexpectedResponse(response);
        }
    }

    public async Task ClearAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Delete,
            $"api/v1/worlds/{worldId.Value:D}/player-presence",
            cancellationToken);
        var response = await SendAsync(request, cancellationToken);
        if (!string.Equals(response.Code, "PlayerPresenceCleared", StringComparison.Ordinal) &&
            !string.Equals(response.Code, "NotFound", StringComparison.Ordinal))
        {
            throw CreateUnexpectedResponse(response);
        }
    }

    private async Task<HttpRequestMessage> CreateAuthorizedRequestAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        var accessToken = await _accessTokens.GetAccessTokenAsync(cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        if (accessToken.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException("Steward access token unexpectedly contains whitespace.");
        }

        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
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
            "player-presence",
            cancellationToken);
        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Code))
        {
            throw new InvalidDataException("Steward returned an invalid player-presence response.");
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
                $"Steward returned invalid player-presence data for '{response.Code}'.",
                exception);
        }
    }

    private static StewardRemoteApiException CreateUnexpectedResponse(ApiResponse response)
        => new(
            response.StatusCode,
            response.Code,
            response.Retryable || IsTransientStatus(response.StatusCode));

    private static bool IsTransientStatus(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.RequestTimeout ||
           statusCode == HttpStatusCode.TooManyRequests ||
           (int)statusCode >= 500;

    private static void ValidateWorldId(WorldId worldId)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }
    }

    private sealed record PlayerPresenceDto(
        string Provider,
        string ExternalId);

    private sealed record LobbyHostDto(
        string Provider,
        string ExternalId,
        string State)
    {
        public StewardRemoteLobbyHost ToDomain()
        {
            if (!Enum.TryParse<StewardRemoteHostPresenceState>(State, ignoreCase: false, out var state))
            {
                throw new InvalidDataException($"Steward returned unknown lobby Host state '{State}'.");
            }

            return new StewardRemoteLobbyHost(Provider, ExternalId, state);
        }
    }

    private sealed record PlayerPresenceSnapshotDto(
        PlayerPresenceDto[] Players,
        LobbyHostDto? Host);

    private sealed record ApiResponse(
        string Code,
        JsonElement? Data,
        bool Retryable)
    {
        public HttpStatusCode StatusCode { get; init; }
    }
}
