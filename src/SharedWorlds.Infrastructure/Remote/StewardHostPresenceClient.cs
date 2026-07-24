using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Remote;

public enum StewardRemoteHostPresenceState
{
    Starting,
    Ready
}

public sealed record StewardRemoteHostPresence(
    StewardRemoteHostPresenceState State,
    string? Address,
    int? Port,
    string? JoinToken);

public enum PublishStewardRemoteHostPresenceStatus
{
    Published,
    ReservationMismatch,
    NotFoundOrUnauthorized,
    InvalidHostPresence
}

/// <summary>
/// Transport client for short-lived hosted-session evidence. Host presence is deliberately not
/// writable World authority: readers receive only the connection evidence needed by Join, while
/// publishers must already hold the exact active reservation identified by session and generation.
/// </summary>
public sealed class StewardHostPresenceClient
{
    private readonly HttpClient _apiClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public StewardHostPresenceClient(HttpClient apiClient)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        if (apiClient.BaseAddress is null)
        {
            throw new ArgumentException("Steward API HttpClient requires a BaseAddress.", nameof(apiClient));
        }

        _apiClient = apiClient;
    }

    public async Task<StewardRemoteHostPresence?> GetAsync(
        WorldId worldId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        ValidateAccessToken(accessToken);

        using var request = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"api/v1/worlds/{worldId.Value:D}/host-presence",
            accessToken);
        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "HostPresence" => DeserializeRequiredData<HostPresenceDto>(response).ToDomain(),
            "NotFound" => null,
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    public async Task<PublishStewardRemoteHostPresenceStatus> PublishAsync(
        WorldId worldId,
        Guid reservationSessionId,
        long reservationGeneration,
        StewardRemoteHostPresenceState state,
        string? address,
        int? port,
        string? joinToken,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        ValidateReservationIdentity(reservationSessionId, reservationGeneration);
        ValidateAccessToken(accessToken);

        using var request = CreateAuthorizedRequest(
            HttpMethod.Put,
            $"api/v1/worlds/{worldId.Value:D}/host-presence",
            accessToken);
        request.Content = JsonContent.Create(new PublishHostPresenceRequest(
            reservationSessionId,
            reservationGeneration,
            state.ToString(),
            address,
            port,
            joinToken));

        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "HostPresencePublished" => PublishStewardRemoteHostPresenceStatus.Published,
            "ReservationMismatch" => PublishStewardRemoteHostPresenceStatus.ReservationMismatch,
            "NotFound" => PublishStewardRemoteHostPresenceStatus.NotFoundOrUnauthorized,
            "InvalidHostPresence" => PublishStewardRemoteHostPresenceStatus.InvalidHostPresence,
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    public async Task<bool> ClearAsync(
        WorldId worldId,
        Guid reservationSessionId,
        long reservationGeneration,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        ValidateReservationIdentity(reservationSessionId, reservationGeneration);
        ValidateAccessToken(accessToken);

        using var request = CreateAuthorizedRequest(
            HttpMethod.Delete,
            $"api/v1/worlds/{worldId.Value:D}/host-presence/{reservationSessionId:D}/{reservationGeneration}",
            accessToken);
        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "HostPresenceCleared" => true,
            "NotFound" => false,
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
        var envelope = await RemoteApiJson.DeserializeAsync<ApiResponse>(
            response.Content,
            _jsonOptions,
            "host-presence",
            cancellationToken);

        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Code))
        {
            throw new InvalidDataException("Steward returned an invalid host-presence response.");
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
                $"Steward returned invalid host-presence data for '{response.Code}'.",
                exception);
        }
    }

    private static HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        string uri,
        string accessToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static StewardRemoteApiException CreateUnexpectedResponse(ApiResponse response)
        => new(
            response.StatusCode,
            response.Code,
            response.Retryable || IsTransientStatus(response.StatusCode));

    private static void ValidateWorldId(WorldId worldId)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }
    }

    private static void ValidateReservationIdentity(Guid sessionId, long generation)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Reservation session ID is required.", nameof(sessionId));
        }

        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }
    }

    private static void ValidateAccessToken(string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        if (accessToken.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Access token must not contain whitespace.", nameof(accessToken));
        }
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.RequestTimeout ||
           statusCode == HttpStatusCode.TooManyRequests ||
           (int)statusCode >= 500;

    private sealed record ApiResponse(
        string Code,
        JsonElement? Data,
        bool Retryable)
    {
        public HttpStatusCode StatusCode { get; init; }
    }

    private sealed record PublishHostPresenceRequest(
        Guid ReservationSessionId,
        long ReservationGeneration,
        string State,
        string? Address,
        int? Port,
        string? JoinToken);

    private sealed record HostPresenceDto(
        string State,
        string? Address,
        int? Port,
        string? JoinToken)
    {
        public StewardRemoteHostPresence ToDomain()
        {
            if (!Enum.TryParse<StewardRemoteHostPresenceState>(State, ignoreCase: false, out var state))
            {
                throw new InvalidDataException($"Steward returned unknown host-presence state '{State}'.");
            }

            return new StewardRemoteHostPresence(
                state,
                Address,
                Port,
                JoinToken);
        }
    }
}
