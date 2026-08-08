using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Remote;

public sealed record StewardLegacyAuthorityRetirementEvidence(
    WorldId WorldId,
    Guid SessionId,
    long Generation,
    RevisionId StateRevisionId,
    RevisionId? EnvironmentRevisionId,
    DateTimeOffset RetiredAt);

public interface IStewardLegacyAuthorityRetirementClient
{
    Task<StewardLegacyAuthorityRetirementEvidence?> GetAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default);

    Task<StewardLegacyAuthorityRetirementEvidence> RetireAsync(
        WorldId worldId,
        Guid sessionId,
        long generation,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Temporary typed client for the one-way Backend.Api retirement boundary used during peer-authority
/// migration. The access token identifies the caller and the backend derives the installation from
/// that authenticated session; no identity or installation is accepted from this client as payload.
/// </summary>
public sealed class StewardLegacyAuthorityRetirementClient :
    IStewardLegacyAuthorityRetirementClient
{
    private readonly HttpClient _apiClient;
    private readonly IStewardAccessTokenProvider _accessTokens;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public StewardLegacyAuthorityRetirementClient(
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

    public async Task<StewardLegacyAuthorityRetirementEvidence?> GetAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Get,
            Path(worldId),
            cancellationToken);
        var response = await SendAsync(request, cancellationToken);
        if (string.Equals(response.Code, "LegacyAuthorityNotRetired", StringComparison.Ordinal))
        {
            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                throw CreateUnexpectedResponse(response);
            }

            return null;
        }

        if (!string.Equals(response.Code, "LegacyAuthorityAlreadyRetired", StringComparison.Ordinal) ||
            !IsSuccessStatus(response.StatusCode))
        {
            throw CreateUnexpectedResponse(response);
        }

        return DeserializeEvidence(response, worldId);
    }

    public async Task<StewardLegacyAuthorityRetirementEvidence> RetireAsync(
        WorldId worldId,
        Guid sessionId,
        long generation,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Reservation session ID is required.", nameof(sessionId));
        }

        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Post,
            Path(worldId),
            cancellationToken);
        request.Content = JsonContent.Create(
            new RetireRequest(sessionId, generation),
            options: _jsonOptions);

        var response = await SendAsync(request, cancellationToken);
        var successCode =
            string.Equals(response.Code, "LegacyAuthorityRetired", StringComparison.Ordinal) ||
            string.Equals(response.Code, "LegacyAuthorityAlreadyRetired", StringComparison.Ordinal);
        if (!successCode || !IsSuccessStatus(response.StatusCode))
        {
            throw CreateUnexpectedResponse(response);
        }

        var evidence = DeserializeEvidence(response, worldId);
        if (evidence.SessionId != sessionId || evidence.Generation != generation)
        {
            throw new InvalidDataException(
                "Steward returned legacy authority retirement evidence for a different reservation.");
        }

        return evidence;
    }

    private StewardLegacyAuthorityRetirementEvidence DeserializeEvidence(
        ApiResponse response,
        WorldId expectedWorldId)
    {
        if (response.Data is null || response.Data.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidDataException(
                $"Steward response '{response.Code}' omitted required retirement evidence.");
        }

        RetirementData data;
        try
        {
            data = response.Data.Value.Deserialize<RetirementData>(_jsonOptions)
                   ?? throw new InvalidDataException(
                       $"Steward response '{response.Code}' returned null retirement evidence.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Steward returned invalid retirement evidence for '{response.Code}'.",
                exception);
        }

        if (data.WorldId != expectedWorldId.Value ||
            data.SessionId == Guid.Empty ||
            data.Generation <= 0 ||
            data.StateRevisionId == Guid.Empty ||
            data.RetiredAt == default)
        {
            throw new InvalidDataException("Steward returned malformed legacy authority retirement evidence.");
        }

        return new StewardLegacyAuthorityRetirementEvidence(
            expectedWorldId,
            data.SessionId,
            data.Generation,
            new RevisionId(data.StateRevisionId),
            data.EnvironmentRevisionId is { } environment
                ? new RevisionId(environment)
                : null,
            data.RetiredAt);
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
            "legacy-authority-retirement",
            cancellationToken);
        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Code))
        {
            throw new InvalidDataException("Steward returned an invalid legacy authority retirement response.");
        }

        return envelope with { StatusCode = response.StatusCode };
    }

    private static StewardRemoteApiException CreateUnexpectedResponse(ApiResponse response)
        => new(
            response.StatusCode,
            response.Code,
            response.Retryable || IsTransientStatus(response.StatusCode));

    private static bool IsSuccessStatus(HttpStatusCode statusCode)
        => (int)statusCode is >= 200 and <= 299;

    private static bool IsTransientStatus(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.RequestTimeout ||
           statusCode == HttpStatusCode.TooManyRequests ||
           (int)statusCode >= 500;

    private static string Path(WorldId worldId)
        => $"api/v1/worlds/{worldId.Value:D}/reservation/retire-peer-authority";

    private static void ValidateWorldId(WorldId worldId)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }
    }

    private sealed record RetireRequest(
        Guid SessionId,
        long Generation);

    private sealed record RetirementData(
        Guid WorldId,
        Guid SessionId,
        long Generation,
        Guid StateRevisionId,
        Guid? EnvironmentRevisionId,
        DateTimeOffset RetiredAt);

    private sealed record ApiResponse(
        string Code,
        JsonElement? Data,
        bool Retryable)
    {
        public HttpStatusCode StatusCode { get; init; }
    }
}
