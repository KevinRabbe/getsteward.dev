using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Remote;

public sealed record StewardRemoteIdentity(
    string Provider,
    string ExternalId);

public sealed record StewardRemoteWorldMetadata(
    WorldId WorldId,
    string AdapterId,
    string DisplayName,
    RevisionId CurrentStateRevisionId,
    RevisionId? CurrentEnvironmentRevisionId,
    StewardRemoteIdentity AccessManager,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public StewardRemoteWorldHead Head
        => new(CurrentStateRevisionId, CurrentEnvironmentRevisionId);
}

public sealed record StewardRemoteStateRevisionMetadata(
    RevisionId RevisionId,
    long ByteSize,
    string Sha256,
    RevisionId? RequiredEnvironmentRevisionId,
    DateTimeOffset PublishedAt);

public sealed record StewardRemoteEnvironmentRevisionMetadata(
    RevisionId RevisionId,
    string ArtifactReference,
    long? ByteSize,
    string? Sha256,
    DateTimeOffset PublishedAt);

public sealed record StewardRemoteCurrentRevision(
    StewardRemoteWorldMetadata World,
    StewardRemoteStateRevisionMetadata? State,
    StewardRemoteEnvironmentRevisionMetadata? Environment);

/// <summary>
/// Read-only client for shared World and immutable revision metadata. It returns provider-neutral
/// Infrastructure records and leaves lifecycle, cache, and authority policy to higher layers.
/// </summary>
public sealed class StewardWorldMetadataClient
{
    private readonly HttpClient _apiClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public StewardWorldMetadataClient(HttpClient apiClient)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        if (apiClient.BaseAddress is null)
        {
            throw new ArgumentException("Steward API HttpClient requires a BaseAddress.", nameof(apiClient));
        }

        _apiClient = apiClient;
    }

    public async Task<IReadOnlyList<StewardRemoteWorldMetadata>> ListWorldsAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ValidateAccessToken(accessToken);
        using var request = CreateAuthorizedRequest(HttpMethod.Get, "api/v1/worlds", accessToken);
        var response = await SendAsync(request, cancellationToken);
        if (!string.Equals(response.Code, "WorldsListed", StringComparison.Ordinal))
        {
            throw CreateUnexpectedResponse(response);
        }

        var data = DeserializeRequiredData<WorldDto[]>(response);
        return data.Select(static world => world.ToDomain()).ToArray();
    }

    public async Task<StewardRemoteWorldMetadata?> GetWorldAsync(
        WorldId worldId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        ValidateAccessToken(accessToken);
        using var request = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"api/v1/worlds/{worldId.Value:D}",
            accessToken);
        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "WorldFound" => DeserializeRequiredData<WorldDto>(response).ToDomain(),
            "WorldNotFound" => null,
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    public async Task<StewardRemoteCurrentRevision?> GetCurrentRevisionAsync(
        WorldId worldId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        ValidateAccessToken(accessToken);
        using var request = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"api/v1/worlds/{worldId.Value:D}/current-revision",
            accessToken);
        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "CurrentRevisionFound" => DeserializeRequiredData<CurrentRevisionDto>(response).ToDomain(),
            "WorldNotFound" => null,
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    public async Task<StewardRemoteStateRevisionMetadata?> GetStateRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        ValidateRevisionId(revisionId);
        ValidateAccessToken(accessToken);
        using var request = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"api/v1/worlds/{worldId.Value:D}/revisions/{revisionId.Value:D}/state",
            accessToken);
        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "StateRevisionFound" => DeserializeRequiredData<StateRevisionDto>(response).ToDomain(),
            "RevisionNotFoundOrUnauthorized" => null,
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    public async Task<StewardRemoteEnvironmentRevisionMetadata?> GetEnvironmentRevisionAsync(
        WorldId worldId,
        RevisionId revisionId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        ValidateRevisionId(revisionId);
        ValidateAccessToken(accessToken);
        using var request = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"api/v1/worlds/{worldId.Value:D}/revisions/{revisionId.Value:D}/environment",
            accessToken);
        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "EnvironmentRevisionFound" => DeserializeRequiredData<EnvironmentRevisionDto>(response).ToDomain(),
            "RevisionNotFoundOrUnauthorized" => null,
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
            throw new InvalidDataException("Steward returned malformed World metadata JSON.", exception);
        }

        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Code))
        {
            throw new InvalidDataException("Steward returned an invalid World metadata response.");
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
                $"Steward returned invalid World metadata for '{response.Code}'.",
                exception);
        }
    }

    private static StewardRemoteApiException CreateUnexpectedResponse(ApiResponse response)
        => new(
            response.StatusCode,
            response.Code,
            response.Retryable || IsTransientStatus(response.StatusCode));

    private static HttpRequestMessage CreateAuthorizedRequest(
        HttpMethod method,
        string uri,
        string accessToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static void ValidateWorldId(WorldId worldId)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }
    }

    private static void ValidateRevisionId(RevisionId revisionId)
    {
        if (revisionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Revision ID is required.", nameof(revisionId));
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

    private sealed record IdentityDto(
        string Provider,
        string ExternalId)
    {
        public StewardRemoteIdentity ToDomain() => new(Provider, ExternalId);
    }

    private sealed record WorldDto(
        Guid WorldId,
        string AdapterId,
        string DisplayName,
        Guid CurrentStateRevisionId,
        Guid? CurrentEnvironmentRevisionId,
        IdentityDto AccessManager,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt)
    {
        public StewardRemoteWorldMetadata ToDomain()
            => new(
                new WorldId(WorldId),
                AdapterId,
                DisplayName,
                new RevisionId(CurrentStateRevisionId),
                CurrentEnvironmentRevisionId is { } environment
                    ? new RevisionId(environment)
                    : null,
                AccessManager.ToDomain(),
                CreatedAt,
                UpdatedAt);
    }

    private sealed record StateRevisionDto(
        Guid RevisionId,
        long ByteSize,
        string Sha256,
        Guid? RequiredEnvironmentRevisionId,
        DateTimeOffset PublishedAt)
    {
        public StewardRemoteStateRevisionMetadata ToDomain()
            => new(
                new RevisionId(RevisionId),
                ByteSize,
                Sha256,
                RequiredEnvironmentRevisionId is { } environment
                    ? new RevisionId(environment)
                    : null,
                PublishedAt);
    }

    private sealed record EnvironmentRevisionDto(
        Guid RevisionId,
        string ArtifactReference,
        long? ByteSize,
        string? Sha256,
        DateTimeOffset PublishedAt)
    {
        public StewardRemoteEnvironmentRevisionMetadata ToDomain()
            => new(
                new RevisionId(RevisionId),
                ArtifactReference,
                ByteSize,
                Sha256,
                PublishedAt);
    }

    private sealed record CurrentRevisionDto(
        WorldDto World,
        StateRevisionDto? State,
        EnvironmentRevisionDto? Environment)
    {
        public StewardRemoteCurrentRevision ToDomain()
            => new(
                World.ToDomain(),
                State?.ToDomain(),
                Environment?.ToDomain());
    }
}
