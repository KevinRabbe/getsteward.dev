using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Infrastructure.Remote;

public enum RemotePrivateSnapshotRevisionEvidenceStatus
{
    Published,
    AlreadyPublished,
    NotFoundOrUnauthorized,
    InvalidRequest,
    Conflict
}

public sealed record RemotePrivateSnapshotRevisionEvidenceResult(
    RemotePrivateSnapshotRevisionEvidenceStatus Status,
    WorldId WorldId,
    RevisionId StateRevisionId,
    RevisionId EnvironmentRevisionId,
    DateTimeOffset? RecordedAt);

/// <summary>
/// Narrow authenticated client for publishing the exact immutable revision records behind already
/// verified owner-private snapshot bytes. Owner, source installation, and evidence time remain
/// backend-derived; the client sends only the exact World, StateRevision, and EnvironmentRevision.
/// </summary>
public sealed class StewardPrivateSnapshotRevisionEvidenceClient
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _apiClient;
    private readonly Func<CancellationToken, Task<string?>> _accessTokenProvider;

    public StewardPrivateSnapshotRevisionEvidenceClient(
        HttpClient apiClient,
        Func<CancellationToken, Task<string?>> accessTokenProvider)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(accessTokenProvider);
        if (apiClient.BaseAddress is null)
        {
            throw new ArgumentException(
                "Steward API HttpClient requires a BaseAddress.",
                nameof(apiClient));
        }

        _ = StewardRemoteEndpointPolicy.NormalizeApiBaseAddress(apiClient.BaseAddress);
        _apiClient = apiClient;
        _accessTokenProvider = accessTokenProvider;
    }

    public async Task<RemotePrivateSnapshotRevisionEvidenceResult> PublishAsync(
        WorldId worldId,
        StateRevision stateRevision,
        EnvironmentRevision environmentRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stateRevision);
        ArgumentNullException.ThrowIfNull(environmentRevision);
        ValidateExactRecords(worldId, stateRevision, environmentRevision);

        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Post,
            $"api/v1/private-worlds/{worldId.Value:D}/snapshot-revision-evidence",
            cancellationToken);
        request.Content = JsonContent.Create(new PublishRequest(
            stateRevision,
            environmentRevision));

        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "PrivateSnapshotRevisionEvidencePublished" => Success(
                response,
                RemotePrivateSnapshotRevisionEvidenceStatus.Published,
                worldId,
                stateRevision.Id,
                environmentRevision.Id),
            "PrivateSnapshotRevisionEvidenceAlreadyPublished" => Success(
                response,
                RemotePrivateSnapshotRevisionEvidenceStatus.AlreadyPublished,
                worldId,
                stateRevision.Id,
                environmentRevision.Id),
            "PrivateSnapshotNotFound" => Terminal(
                response,
                HttpStatusCode.NotFound,
                RemotePrivateSnapshotRevisionEvidenceStatus.NotFoundOrUnauthorized,
                worldId,
                stateRevision.Id,
                environmentRevision.Id),
            "PrivateSnapshotRevisionEvidenceInvalid" => Terminal(
                response,
                HttpStatusCode.BadRequest,
                RemotePrivateSnapshotRevisionEvidenceStatus.InvalidRequest,
                worldId,
                stateRevision.Id,
                environmentRevision.Id),
            "PrivateSnapshotRevisionEvidenceConflict" => Terminal(
                response,
                HttpStatusCode.Conflict,
                RemotePrivateSnapshotRevisionEvidenceStatus.Conflict,
                worldId,
                stateRevision.Id,
                environmentRevision.Id),
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    private static RemotePrivateSnapshotRevisionEvidenceResult Success(
        ApiResponse response,
        RemotePrivateSnapshotRevisionEvidenceStatus status,
        WorldId worldId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId)
    {
        RequireStatus(response, HttpStatusCode.OK);
        var data = DeserializeRequiredData<EvidenceData>(response);
        if (data.WorldId != worldId.Value ||
            data.StateRevisionId != stateRevisionId.Value ||
            data.EnvironmentRevisionId != environmentRevisionId.Value ||
            data.RecordedAt == default)
        {
            throw new InvalidDataException(
                "Steward returned private revision evidence for the wrong or invalid exact head.");
        }

        return new(
            status,
            worldId,
            stateRevisionId,
            environmentRevisionId,
            data.RecordedAt);
    }

    private static RemotePrivateSnapshotRevisionEvidenceResult Terminal(
        ApiResponse response,
        HttpStatusCode expectedStatus,
        RemotePrivateSnapshotRevisionEvidenceStatus status,
        WorldId worldId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId)
    {
        RequireStatus(response, expectedStatus);
        if (response.Data is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined })
        {
            throw new InvalidDataException(
                $"Steward terminal response '{response.Code}' unexpectedly included evidence data.");
        }

        return new(
            status,
            worldId,
            stateRevisionId,
            environmentRevisionId,
            RecordedAt: null);
    }

    private async Task<HttpRequestMessage> CreateAuthorizedRequestAsync(
        HttpMethod method,
        string uri,
        CancellationToken cancellationToken)
    {
        var token = await _accessTokenProvider(cancellationToken);
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException(
                "An authenticated Steward session is required before private revision evidence publication.");
        }

        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
            JsonOptions,
            "private-snapshot-revision-evidence",
            cancellationToken);
        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Code))
        {
            throw new InvalidDataException(
                "Steward returned an invalid private revision evidence response.");
        }

        return envelope with { StatusCode = response.StatusCode };
    }

    private static T DeserializeRequiredData<T>(ApiResponse response)
    {
        if (response.Data is null ||
            response.Data.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidDataException(
                $"Steward response '{response.Code}' omitted required evidence data.");
        }

        try
        {
            return response.Data.Value.Deserialize<T>(JsonOptions)
                   ?? throw new InvalidDataException(
                       $"Steward response '{response.Code}' returned null evidence data.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Steward returned invalid evidence data for '{response.Code}'.",
                exception);
        }
    }

    private static void RequireStatus(ApiResponse response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            throw new InvalidDataException(
                $"Steward response '{response.Code}' used HTTP {(int)response.StatusCode} instead of expected {(int)expected}.");
        }
    }

    private static void ValidateExactRecords(
        WorldId worldId,
        StateRevision stateRevision,
        EnvironmentRevision environmentRevision)
    {
        var validation = new OwnedWorldSnapshotRevisionEvidence(
            "client-validation",
            "client-validation",
            "client-validation",
            worldId,
            stateRevision,
            environmentRevision,
            DateTimeOffset.UtcNow);
        validation.Validate();
    }

    private static StewardRemoteApiException CreateUnexpectedResponse(ApiResponse response)
        => new(
            response.StatusCode,
            response.Code,
            response.Retryable ||
            response.StatusCode == HttpStatusCode.RequestTimeout ||
            response.StatusCode == HttpStatusCode.TooManyRequests ||
            (int)response.StatusCode >= 500);

    private sealed record PublishRequest(
        StateRevision StateRevision,
        EnvironmentRevision EnvironmentRevision);

    private sealed record ApiResponse(
        string Code,
        JsonElement? Data,
        bool Retryable)
    {
        public HttpStatusCode StatusCode { get; init; }
    }

    private sealed record EvidenceData(
        Guid WorldId,
        Guid StateRevisionId,
        Guid EnvironmentRevisionId,
        DateTimeOffset RecordedAt);
}
