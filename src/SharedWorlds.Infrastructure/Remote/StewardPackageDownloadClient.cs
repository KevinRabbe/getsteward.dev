using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Remote;

public enum RemotePackageKind
{
    State,
    Environment
}

public sealed class StewardRemoteApiException : IOException
{
    public StewardRemoteApiException(
        HttpStatusCode statusCode,
        string code,
        bool retryable)
        : base($"Steward API request failed with '{code}' ({(int)statusCode}).")
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
/// Narrow client for BE-3 package download authorization. It deliberately does not perform the
/// object-store download itself; VerifiedPackageCache uses a separate HttpClient for that transfer.
/// </summary>
public sealed class StewardPackageDownloadClient
{
    private readonly HttpClient _apiClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public StewardPackageDownloadClient(HttpClient apiClient)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        if (apiClient.BaseAddress is null)
        {
            throw new ArgumentException("Steward API HttpClient requires a BaseAddress.", nameof(apiClient));
        }

        _apiClient = apiClient;
    }

    public async Task<AuthorizedPackageDownload> AuthorizeDownloadAsync(
        WorldId worldId,
        RevisionId revisionId,
        RemotePackageKind kind,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }

        if (revisionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Revision ID is required.", nameof(revisionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        if (accessToken.Contains(' '))
        {
            throw new ArgumentException("Access token must not contain whitespace separators.", nameof(accessToken));
        }

        var kindSegment = kind switch
        {
            RemotePackageKind.State => "state",
            RemotePackageKind.Environment => "environment",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/worlds/{worldId.Value:D}/revisions/{revisionId.Value:D}/{kindSegment}/download-authorization");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _apiClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var envelope = await JsonSerializer.DeserializeAsync<ApiEnvelope>(
            responseStream,
            _jsonOptions,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new StewardRemoteApiException(
                response.StatusCode,
                envelope?.Code ?? "UnknownApiFailure",
                envelope?.Retryable ?? IsTransientStatus(response.StatusCode));
        }

        if (envelope is null ||
            !string.Equals(envelope.Code, "DownloadAuthorized", StringComparison.Ordinal) ||
            envelope.Data?.Authorization is null)
        {
            throw new InvalidDataException("Steward returned an invalid download-authorization response.");
        }

        var data = envelope.Data;
        var authorization = data.Authorization;
        if (!string.Equals(authorization.Method, "GET", StringComparison.OrdinalIgnoreCase) ||
            data.ExpectedByteSize <= 0 ||
            authorization.ExpectedByteSize != data.ExpectedByteSize ||
            !IsSha256(data.ExpectedSha256))
        {
            throw new InvalidDataException("Steward returned inconsistent immutable package metadata.");
        }

        return new AuthorizedPackageDownload(
            new Uri(authorization.Uri, UriKind.Absolute),
            authorization.RequiredHeaders ?? new Dictionary<string, string>(),
            authorization.ExpiresAt,
            data.ExpectedByteSize,
            data.ExpectedSha256.ToUpperInvariant());
    }

    private static bool IsSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f') ||
                  (character >= 'A' && character <= 'F')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.RequestTimeout ||
           statusCode == HttpStatusCode.TooManyRequests ||
           (int)statusCode >= 500;

    private sealed record ApiEnvelope(
        string Code,
        DownloadData? Data,
        bool Retryable);

    private sealed record DownloadData(
        DownloadAuthorization Authorization,
        long ExpectedByteSize,
        string ExpectedSha256);

    private sealed record DownloadAuthorization(
        string Uri,
        string Method,
        Dictionary<string, string>? RequiredHeaders,
        DateTimeOffset ExpiresAt,
        long ExpectedByteSize);
}
