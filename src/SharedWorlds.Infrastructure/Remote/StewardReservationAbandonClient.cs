using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Remote;

public enum RemoteReservationAbandonStatus
{
    Abandoned,
    NoLongerCurrent
}

/// <summary>
/// Transport client for explicitly relinquishing an exact reservation before writable gameplay
/// starts. The backend operation is naturally convergent and therefore requires no idempotency key.
/// </summary>
public sealed class StewardReservationAbandonClient
{
    private readonly HttpClient _apiClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public StewardReservationAbandonClient(HttpClient apiClient)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        if (apiClient.BaseAddress is null)
        {
            throw new ArgumentException("Steward API HttpClient requires a BaseAddress.", nameof(apiClient));
        }

        _apiClient = apiClient;
    }

    public async Task<RemoteReservationAbandonStatus> AbandonAsync(
        WorldId worldId,
        string installationId,
        Guid sessionId,
        long generation,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session ID is required.", nameof(sessionId));
        }

        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        ValidateAccessToken(accessToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/worlds/{worldId.Value:D}/reservation/abandon");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new AbandonRequest(installationId, sessionId, generation));

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
            throw new InvalidDataException("Steward returned malformed reservation-abandon JSON.", exception);
        }

        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Code))
        {
            throw new InvalidDataException("Steward returned an invalid reservation-abandon response.");
        }

        return envelope.Code switch
        {
            "ReservationAbandoned" => RemoteReservationAbandonStatus.Abandoned,
            "ReservationNoLongerCurrent" => RemoteReservationAbandonStatus.NoLongerCurrent,
            _ => throw new StewardRemoteApiException(
                response.StatusCode,
                envelope.Code,
                envelope.Retryable || IsTransientStatus(response.StatusCode))
        };
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

    private sealed record AbandonRequest(
        string InstallationId,
        Guid SessionId,
        long Generation);

    private sealed record ApiResponse(
        string Code,
        bool Retryable);
}
