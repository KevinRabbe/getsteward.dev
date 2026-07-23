using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Remote;

public enum RemoteReservationAcquireStatus
{
    Acquired,
    AlreadyHeldByCaller,
    WorldBusy,
    WorldUncertain,
    HeadChanged,
    NotFoundOrUnauthorized,
    IdempotencyKeyConflict
}

public enum RemoteReservationHeartbeatStatus
{
    Accepted,
    ReservationMismatch
}

public enum RemoteReservationReclaimStatus
{
    Reclaimed,
    GracePeriodRequired,
    ReservationMismatch,
    NotFoundOrUnauthorized,
    IdempotencyKeyConflict
}

public enum RemoteWorldCommitStatus
{
    Committed,
    Unchanged,
    HeadChanged,
    ReservationMismatch,
    InvalidCandidate,
    IdempotencyKeyConflict
}

public sealed record StewardRemoteWorldHead(
    RevisionId StateRevisionId,
    RevisionId? EnvironmentRevisionId);

public sealed record StewardRemoteReservation(
    WorldId WorldId,
    Guid SessionId,
    long Generation,
    string HolderProvider,
    string HolderExternalId,
    string InstallationId,
    StewardRemoteWorldHead StartingHead,
    string State,
    DateTimeOffset AcquiredAt,
    DateTimeOffset LastHeartbeatAt,
    DateTimeOffset? BecameUncertainAt);

public sealed record RemoteReservationAcquireResult(
    RemoteReservationAcquireStatus Status,
    StewardRemoteReservation? Reservation = null,
    StewardRemoteWorldHead? CurrentHead = null);

public sealed record RemoteReservationReclaimResult(
    RemoteReservationReclaimStatus Status,
    long? InvalidatedGeneration = null);

public sealed record RemoteWorldCommitResult(
    RemoteWorldCommitStatus Status,
    StewardRemoteWorldHead? CurrentHead = null,
    StewardRemoteWorldHead? CandidateHead = null);

/// <summary>
/// Transport-only client for the BE-4 one-writer authority API. It deliberately does not decide
/// lifecycle or recovery policy; callers map the stable backend result codes onto Core behavior.
/// </summary>
public sealed class StewardAuthorityClient
{
    private readonly HttpClient _apiClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public StewardAuthorityClient(HttpClient apiClient)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        if (apiClient.BaseAddress is null)
        {
            throw new ArgumentException("Steward API HttpClient requires a BaseAddress.", nameof(apiClient));
        }

        _apiClient = apiClient;
    }

    public async Task<RemoteReservationAcquireResult> AcquireAsync(
        WorldId worldId,
        string installationId,
        StewardRemoteWorldHead expectedHead,
        string accessToken,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        ValidateHead(expectedHead);
        ValidateAccessToken(accessToken);
        ValidateIdempotencyKey(idempotencyKey);

        using var request = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"api/v1/worlds/{worldId.Value:D}/reservation/acquire",
            accessToken,
            idempotencyKey);
        request.Content = JsonContent.Create(new AcquireRequest(
            installationId,
            expectedHead.StateRevisionId.Value,
            expectedHead.EnvironmentRevisionId?.Value));

        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "ReservationAcquired" => new(
                RemoteReservationAcquireStatus.Acquired,
                DeserializeData<ReservationDto>(response)?.ToDomain()),
            "ReservationAlreadyHeldByCaller" => new(
                RemoteReservationAcquireStatus.AlreadyHeldByCaller,
                DeserializeData<ReservationDto>(response)?.ToDomain()),
            "WorldBusy" => new(
                RemoteReservationAcquireStatus.WorldBusy,
                DeserializeData<ReservationDto>(response)?.ToDomain()),
            "WorldUncertain" => new(
                RemoteReservationAcquireStatus.WorldUncertain,
                DeserializeData<ReservationDto>(response)?.ToDomain()),
            "HeadChanged" => new(
                RemoteReservationAcquireStatus.HeadChanged,
                CurrentHead: DeserializeData<HeadDto>(response)?.ToDomain()),
            "WorldNotFoundOrUnauthorized" => new(RemoteReservationAcquireStatus.NotFoundOrUnauthorized),
            "IdempotencyKeyConflict" => new(RemoteReservationAcquireStatus.IdempotencyKeyConflict),
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    public async Task<StewardRemoteReservation?> GetReservationAsync(
        WorldId worldId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        ValidateAccessToken(accessToken);

        using var request = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"api/v1/worlds/{worldId.Value:D}/reservation",
            accessToken);
        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "ReservationFound" => DeserializeRequiredData<ReservationDto>(response).ToDomain(),
            "ReservationNotFoundOrUnauthorized" => null,
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    public async Task<RemoteReservationHeartbeatStatus> HeartbeatAsync(
        WorldId worldId,
        string installationId,
        Guid sessionId,
        long generation,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        ValidateSession(sessionId, generation);
        ValidateAccessToken(accessToken);

        using var request = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"api/v1/worlds/{worldId.Value:D}/reservation/heartbeat",
            accessToken);
        request.Content = JsonContent.Create(new HeartbeatRequest(installationId, sessionId, generation));
        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "HeartbeatAccepted" => RemoteReservationHeartbeatStatus.Accepted,
            "ReservationMismatch" => RemoteReservationHeartbeatStatus.ReservationMismatch,
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    public async Task<RemoteReservationReclaimResult> ReclaimAsync(
        WorldId worldId,
        Guid expectedSessionId,
        long expectedGeneration,
        string accessToken,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        ValidateSession(expectedSessionId, expectedGeneration);
        ValidateAccessToken(accessToken);
        ValidateIdempotencyKey(idempotencyKey);

        using var request = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"api/v1/worlds/{worldId.Value:D}/reservation/reclaim",
            accessToken,
            idempotencyKey);
        request.Content = JsonContent.Create(new ReclaimRequest(expectedSessionId, expectedGeneration));
        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "ReservationReclaimed" => new(
                RemoteReservationReclaimStatus.Reclaimed,
                DeserializeRequiredData<ReclaimedReservationDto>(response).InvalidatedGeneration),
            "GracePeriodRequired" => new(RemoteReservationReclaimStatus.GracePeriodRequired),
            "ReservationMismatch" => new(RemoteReservationReclaimStatus.ReservationMismatch),
            "WorldNotFoundOrUnauthorized" => new(RemoteReservationReclaimStatus.NotFoundOrUnauthorized),
            "IdempotencyKeyConflict" => new(RemoteReservationReclaimStatus.IdempotencyKeyConflict),
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    public async Task<RemoteWorldCommitResult> CommitAsync(
        WorldId worldId,
        string installationId,
        Guid sessionId,
        long generation,
        StewardRemoteWorldHead expectedHead,
        RevisionId candidateStateRevisionId,
        RevisionId? candidateEnvironmentRevisionId,
        string accessToken,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        ValidateSession(sessionId, generation);
        ValidateHead(expectedHead);
        if (candidateStateRevisionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Candidate state revision is required.", nameof(candidateStateRevisionId));
        }

        if (candidateEnvironmentRevisionId is { Value: var environmentValue } && environmentValue == Guid.Empty)
        {
            throw new ArgumentException("Candidate environment revision cannot be empty.", nameof(candidateEnvironmentRevisionId));
        }

        ValidateAccessToken(accessToken);
        ValidateIdempotencyKey(idempotencyKey);

        using var request = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"api/v1/worlds/{worldId.Value:D}/reservation/commit",
            accessToken,
            idempotencyKey);
        request.Content = JsonContent.Create(new CommitRequest(
            installationId,
            sessionId,
            generation,
            expectedHead.StateRevisionId.Value,
            expectedHead.EnvironmentRevisionId?.Value,
            candidateStateRevisionId.Value,
            candidateEnvironmentRevisionId?.Value));
        var response = await SendAsync(request, cancellationToken);

        if (response.Code == "IdempotencyKeyConflict")
        {
            return new(RemoteWorldCommitStatus.IdempotencyKeyConflict);
        }

        var data = DeserializeData<CommitResultDto>(response);
        return response.Code switch
        {
            "Committed" => FromCommit(RemoteWorldCommitStatus.Committed, data),
            "Unchanged" => FromCommit(RemoteWorldCommitStatus.Unchanged, data),
            "HeadChanged" => FromCommit(RemoteWorldCommitStatus.HeadChanged, data),
            "ReservationMismatch" => FromCommit(RemoteWorldCommitStatus.ReservationMismatch, data),
            "InvalidCandidate" => FromCommit(RemoteWorldCommitStatus.InvalidCandidate, data),
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    private static RemoteWorldCommitResult FromCommit(
        RemoteWorldCommitStatus status,
        CommitResultDto? data)
        => new(
            status,
            data?.CurrentHead.ToDomain(),
            data?.CandidateHead?.ToDomain());

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
            throw new InvalidDataException("Steward returned malformed authority JSON.", exception);
        }

        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Code))
        {
            throw new InvalidDataException("Steward returned an invalid authority response.");
        }

        return envelope with { StatusCode = response.StatusCode };
    }

    private T? DeserializeData<T>(ApiResponse response)
    {
        if (response.Data is null || response.Data.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return default;
        }

        try
        {
            return response.Data.Value.Deserialize<T>(_jsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Steward returned invalid authority data for '{response.Code}'.",
                exception);
        }
    }

    private T DeserializeRequiredData<T>(ApiResponse response)
        => DeserializeData<T>(response)
           ?? throw new InvalidDataException($"Steward response '{response.Code}' omitted required data.");

    private static StewardRemoteApiException CreateUnexpectedResponse(ApiResponse response)
        => new(
            response.StatusCode,
            response.Code,
            response.Retryable || IsTransientStatus(response.StatusCode));

    private static HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        string uri,
        string accessToken,
        string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    private static void ValidateWorldId(WorldId worldId)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }
    }

    private static void ValidateHead(StewardRemoteWorldHead head)
    {
        ArgumentNullException.ThrowIfNull(head);
        if (head.StateRevisionId.Value == Guid.Empty)
        {
            throw new ArgumentException("State revision is required.", nameof(head));
        }

        if (head.EnvironmentRevisionId is { Value: var environmentValue } && environmentValue == Guid.Empty)
        {
            throw new ArgumentException("Environment revision cannot be empty.", nameof(head));
        }
    }

    private static void ValidateSession(Guid sessionId, long generation)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session ID is required.", nameof(sessionId));
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

    private static void ValidateIdempotencyKey(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (idempotencyKey.Length > 128 || idempotencyKey.Any(character => character is < '!' or > '~'))
        {
            throw new ArgumentException(
                "Idempotency key must contain at most 128 visible ASCII characters.",
                nameof(idempotencyKey));
        }
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.RequestTimeout ||
           statusCode == HttpStatusCode.TooManyRequests ||
           (int)statusCode >= 500;

    private sealed record AcquireRequest(
        string InstallationId,
        Guid ExpectedStateRevisionId,
        Guid? ExpectedEnvironmentRevisionId);

    private sealed record HeartbeatRequest(
        string InstallationId,
        Guid SessionId,
        long Generation);

    private sealed record ReclaimRequest(
        Guid ExpectedSessionId,
        long ExpectedGeneration);

    private sealed record CommitRequest(
        string InstallationId,
        Guid SessionId,
        long Generation,
        Guid ExpectedStateRevisionId,
        Guid? ExpectedEnvironmentRevisionId,
        Guid CandidateStateRevisionId,
        Guid? CandidateEnvironmentRevisionId);

    private sealed record ApiResponse(
        string Code,
        JsonElement? Data,
        bool Retryable)
    {
        public HttpStatusCode StatusCode { get; init; }
    }

    private sealed record IdentityDto(string Provider, string ExternalId);

    private sealed record HeadDto(Guid StateRevisionId, Guid? EnvironmentRevisionId)
    {
        public StewardRemoteWorldHead ToDomain()
            => new(
                new RevisionId(StateRevisionId),
                EnvironmentRevisionId is { } environment
                    ? new RevisionId(environment)
                    : null);
    }

    private sealed record ReservationDto(
        Guid WorldId,
        Guid SessionId,
        long Generation,
        IdentityDto Holder,
        string InstallationId,
        HeadDto StartingHead,
        string State,
        DateTimeOffset AcquiredAt,
        DateTimeOffset LastHeartbeatAt,
        DateTimeOffset? BecameUncertainAt)
    {
        public StewardRemoteReservation ToDomain()
            => new(
                new WorldId(WorldId),
                SessionId,
                Generation,
                Holder.Provider,
                Holder.ExternalId,
                InstallationId,
                StartingHead.ToDomain(),
                State,
                AcquiredAt,
                LastHeartbeatAt,
                BecameUncertainAt);
    }

    private sealed record ReclaimedReservationDto(long InvalidatedGeneration);

    private sealed record CommitResultDto(
        HeadDto CurrentHead,
        HeadDto? CandidateHead);
}
