using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Remote;

public enum RemoteWorldCreationStatus
{
    Created,
    AlreadyExists
}

public sealed class StewardWorldCreationException : InvalidOperationException
{
    public StewardWorldCreationException(
        HttpStatusCode statusCode,
        string code,
        bool retryable)
        : base($"Steward World creation failed with '{code}' (HTTP {(int)statusCode}).")
    {
        StatusCode = statusCode;
        Code = code;
        Retryable = retryable;
    }

    public HttpStatusCode StatusCode { get; }
    public string Code { get; }
    public bool Retryable { get; }
}

/// <summary>
/// Transport-only client for creating the backend identity of an already-existing local World.
/// Initial immutable environment/state publication remains a separate resumable operation so package
/// bytes can continue to move directly between Desktop and object storage.
/// </summary>
public sealed class StewardWorldCreationClient
{
    private readonly HttpClient _apiClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public StewardWorldCreationClient(HttpClient apiClient)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        if (apiClient.BaseAddress is null)
        {
            throw new ArgumentException("Steward API HttpClient requires a BaseAddress.", nameof(apiClient));
        }

        _apiClient = apiClient;
    }

    public async Task<RemoteWorldCreationStatus> CreateAsync(
        World world,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ValidateAccessToken(accessToken);
        if (world.Id.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(world));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(world.GameAdapterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(world.Name);
        var stateId = world.CurrentStateRevisionId
            ?? throw new ArgumentException("World must have a current state revision before it can be shared.", nameof(world));
        var environmentId = world.CurrentEnvironmentRevisionId
            ?? throw new ArgumentException("World must have a current environment revision before it can be shared.", nameof(world));

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/worlds");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new CreateWorldRequest(
            world.Id.Value,
            world.GameAdapterId,
            world.Name,
            stateId.Value,
            environmentId.Value));

        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "WorldCreated" => RemoteWorldCreationStatus.Created,
            "WorldAlreadyExists" => RemoteWorldCreationStatus.AlreadyExists,
            _ => throw new StewardWorldCreationException(
                response.StatusCode,
                response.Code,
                response.Retryable || IsTransientStatus(response.StatusCode))
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
            throw new InvalidDataException("Steward returned malformed World-creation JSON.", exception);
        }

        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Code))
        {
            throw new InvalidDataException("Steward returned an invalid World-creation response.");
        }

        return envelope with { StatusCode = response.StatusCode };
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

    private sealed record CreateWorldRequest(
        Guid WorldId,
        string AdapterId,
        string DisplayName,
        Guid CurrentStateRevisionId,
        Guid CurrentEnvironmentRevisionId);

    private sealed record ApiResponse(
        string Code,
        JsonElement? Data,
        bool Retryable)
    {
        public HttpStatusCode StatusCode { get; init; }
    }
}
