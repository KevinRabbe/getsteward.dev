using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Infrastructure.Remote;

public enum RemotePrivateSnapshotMaterializationStatus
{
    Authorized,
    NotFoundOrUnauthorized,
    Unavailable,
    AlreadyHere,
    Conflict,
    StorageIntegrityFailure
}

public sealed record RemotePrivateSnapshotMaterializationPlan(
    WorldId WorldId,
    string SourceInstallationId,
    RevisionId StateRevisionId,
    RevisionId EnvironmentRevisionId,
    string GameAdapterId,
    long ExpectedByteSize,
    string ExpectedSha256,
    EnvironmentManifest EnvironmentManifest,
    StateRevision StateRevision,
    EnvironmentRevision EnvironmentRevision,
    AuthorizedPackageDownload Authorization);

public sealed record RemotePrivateSnapshotMaterializationResult(
    RemotePrivateSnapshotMaterializationStatus Status,
    RemotePrivateSnapshotMaterializationPlan? Plan,
    string? Reason);

public sealed record RemoteVerifiedPrivateSnapshotMaterialization(
    RemotePrivateSnapshotMaterializationPlan Plan,
    VerifiedCachedPackage Package);

/// <summary>
/// Authenticated client for preparing one owner-private World for exact local materialization. A signed
/// URL authorizes only bytes; the client independently verifies the selected World/head, complete
/// immutable revision records, environment manifest, package identity, and required transfer headers
/// before publishing the package into the existing verified cache.
/// </summary>
public sealed class StewardPrivateSnapshotMaterializationClient
{
    private const int MaximumRequiredHeaders = 64;
    private const int MaximumHeaderTextLength = 4096;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ForbiddenTransferHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Authorization",
            "Connection",
            "Content-Length",
            "Host",
            "Proxy-Authorization",
            "Range",
            "TE",
            "Trailer",
            "Transfer-Encoding",
            "Upgrade"
        };

    private readonly HttpClient _apiClient;
    private readonly VerifiedPackageCache _cache;
    private readonly Func<CancellationToken, Task<string?>> _accessTokenProvider;

    public StewardPrivateSnapshotMaterializationClient(
        HttpClient apiClient,
        VerifiedPackageCache cache,
        Func<CancellationToken, Task<string?>> accessTokenProvider)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(accessTokenProvider);
        if (apiClient.BaseAddress is null)
        {
            throw new ArgumentException(
                "Steward API HttpClient requires a BaseAddress.",
                nameof(apiClient));
        }

        _ = StewardRemoteEndpointPolicy.NormalizeApiBaseAddress(apiClient.BaseAddress);
        _apiClient = apiClient;
        _cache = cache;
        _accessTokenProvider = accessTokenProvider;
    }

    public async Task<RemotePrivateSnapshotMaterializationResult> AuthorizeAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Get,
            $"api/v1/private-worlds/{worldId.Value:D}/materialization-download",
            cancellationToken);
        var response = await SendAsync(request, cancellationToken);
        return response.Code switch
        {
            "PrivateSnapshotMaterializationDownloadAuthorized" => Authorized(
                response,
                worldId),
            "PrivateSnapshotNotFound" => Terminal(
                response,
                HttpStatusCode.NotFound,
                RemotePrivateSnapshotMaterializationStatus.NotFoundOrUnauthorized),
            "PrivateSnapshotMaterializationUnavailable" => ReasonResult(
                response,
                RemotePrivateSnapshotMaterializationStatus.Unavailable),
            "PrivateSnapshotAlreadyHere" => ReasonResult(
                response,
                RemotePrivateSnapshotMaterializationStatus.AlreadyHere),
            "PrivateSnapshotHeadConflict" => ReasonResult(
                response,
                RemotePrivateSnapshotMaterializationStatus.Conflict),
            "PrivateSnapshotStorageIntegrityFailure" => ReasonResult(
                response,
                RemotePrivateSnapshotMaterializationStatus.StorageIntegrityFailure),
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    public async Task<RemoteVerifiedPrivateSnapshotMaterialization?> EnsureDownloadedAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        var authorization = await AuthorizeAsync(worldId, cancellationToken);
        if (authorization.Status != RemotePrivateSnapshotMaterializationStatus.Authorized)
        {
            return null;
        }

        var plan = authorization.Plan
            ?? throw new InvalidDataException(
                "Private snapshot materialization authorization omitted its exact plan.");
        var cached = await _cache.EnsureAsync(plan.Authorization, cancellationToken);
        if (cached.ByteSize != plan.ExpectedByteSize ||
            !string.Equals(
                cached.Sha256,
                plan.ExpectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageIntegrityException(
                "Verified materialization cache result disagrees with the authorized exact package identity.");
        }

        return new(plan, cached);
    }

    private static RemotePrivateSnapshotMaterializationResult Authorized(
        ApiResponse response,
        WorldId requestedWorldId)
    {
        RequireStatus(response, HttpStatusCode.OK);
        var plan = DeserializeRequiredData<MaterializationPlanDto>(response)
            .ToDomain(requestedWorldId);
        return new(
            RemotePrivateSnapshotMaterializationStatus.Authorized,
            plan,
            Reason: null);
    }

    private static RemotePrivateSnapshotMaterializationResult Terminal(
        ApiResponse response,
        HttpStatusCode expectedStatus,
        RemotePrivateSnapshotMaterializationStatus status)
    {
        RequireStatus(response, expectedStatus);
        if (response.Data is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined })
        {
            throw new InvalidDataException(
                $"Steward terminal response '{response.Code}' unexpectedly included materialization data.");
        }

        return new(status, Plan: null, Reason: null);
    }

    private static RemotePrivateSnapshotMaterializationResult ReasonResult(
        ApiResponse response,
        RemotePrivateSnapshotMaterializationStatus status)
    {
        RequireStatus(response, HttpStatusCode.Conflict);
        var reason = DeserializeRequiredData<ReasonDto>(response).Reason;
        ValidateText(
            reason,
            "Private materialization reason",
            1024,
            allowWhitespace: true);
        return new(status, Plan: null, reason);
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
                "An authenticated Steward session is required before private World materialization preparation.");
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
            "private-snapshot-materialization",
            cancellationToken);
        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Code))
        {
            throw new InvalidDataException(
                "Steward returned an invalid private materialization response.");
        }

        return envelope with { StatusCode = response.StatusCode };
    }

    private static T DeserializeRequiredData<T>(ApiResponse response)
    {
        if (response.Data is null ||
            response.Data.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidDataException(
                $"Steward response '{response.Code}' omitted required materialization data.");
        }

        try
        {
            return response.Data.Value.Deserialize<T>(JsonOptions)
                   ?? throw new InvalidDataException(
                       $"Steward response '{response.Code}' returned null materialization data.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Steward returned invalid materialization data for '{response.Code}'.",
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

    private static void ValidateWorldId(WorldId worldId)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }
    }

    private static void ValidateHeaders(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (headers.Count > MaximumRequiredHeaders)
        {
            throw new InvalidDataException(
                $"Private materialization authorization returned more than {MaximumRequiredHeaders} required headers.");
        }

        foreach (var header in headers)
        {
            ValidateText(header.Key, "Transfer header name", 256);
            if (ForbiddenTransferHeaders.Contains(header.Key))
            {
                throw new InvalidDataException(
                    $"Private materialization authorization attempted to control forbidden header '{header.Key}'.");
            }

            ValidateText(
                header.Value,
                "Transfer header value",
                MaximumHeaderTextLength,
                allowWhitespace: true);
        }
    }

    private static void ValidateText(
        string? value,
        string name,
        int maximumLength,
        bool allowWhitespace = false)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl) ||
            !allowWhitespace && value.Any(char.IsWhiteSpace))
        {
            throw new InvalidDataException(
                $"{name} is invalid or exceeds {maximumLength} characters.");
        }
    }

    private static StewardRemoteApiException CreateUnexpectedResponse(ApiResponse response)
        => new(
            response.StatusCode,
            response.Code,
            response.Retryable ||
            response.StatusCode == HttpStatusCode.RequestTimeout ||
            response.StatusCode == HttpStatusCode.TooManyRequests ||
            (int)response.StatusCode >= 500);

    private sealed record ApiResponse(
        string Code,
        JsonElement? Data,
        bool Retryable)
    {
        public HttpStatusCode StatusCode { get; init; }
    }

    private sealed record MaterializationPlanDto(
        Guid WorldId,
        string? SourceInstallationId,
        Guid StateRevisionId,
        Guid EnvironmentRevisionId,
        string? GameAdapterId,
        long ExpectedByteSize,
        string? ExpectedSha256,
        EnvironmentManifest? EnvironmentManifest,
        StateRevision? StateRevision,
        EnvironmentRevision? EnvironmentRevision,
        DownloadAuthorizationDto? Authorization)
    {
        public RemotePrivateSnapshotMaterializationPlan ToDomain(
            WorldId requestedWorldId)
        {
            ValidateText(
                SourceInstallationId,
                "Source installation ID",
                OwnedWorldSnapshot.MaximumInstallationIdLength,
                allowWhitespace: true);
            if (WorldId != requestedWorldId.Value ||
                StateRevisionId == Guid.Empty ||
                EnvironmentRevisionId == Guid.Empty ||
                ExpectedByteSize is < 1 or > OwnedWorldSnapshot.MaximumStatePackageByteSize ||
                EnvironmentManifest is null ||
                StateRevision is null ||
                EnvironmentRevision is null ||
                Authorization is null)
            {
                throw new InvalidDataException(
                    "Steward returned a materialization plan for the wrong or invalid exact identity.");
            }

            var stateId = new RevisionId(StateRevisionId);
            var environmentId = new RevisionId(EnvironmentRevisionId);
            var adapterId = GameAdapterId ?? string.Empty;
            var sha256 = ExpectedSha256 ?? string.Empty;
            var descriptor = new OwnedWorldSnapshot(
                requestedWorldId,
                "client-validation",
                "client-validation",
                SourceInstallationId!,
                stateId,
                environmentId,
                adapterId,
                "client-validation",
                ExpectedByteSize,
                sha256,
                EnvironmentManifest,
                DateTimeOffset.UtcNow);
            var evidence = new OwnedWorldSnapshotRevisionEvidence(
                "client-validation",
                "client-validation",
                SourceInstallationId!,
                requestedWorldId,
                StateRevision,
                EnvironmentRevision,
                DateTimeOffset.UtcNow);
            evidence.ValidateAgainst(descriptor);
            var authorization = Authorization.ToDomain(
                ExpectedByteSize,
                sha256);
            return new(
                requestedWorldId,
                SourceInstallationId!,
                stateId,
                environmentId,
                adapterId,
                ExpectedByteSize,
                authorization.ExpectedSha256,
                EnvironmentManifest,
                StateRevision,
                EnvironmentRevision,
                authorization);
        }
    }

    private sealed record DownloadAuthorizationDto(
        string? Uri,
        string? Method,
        IReadOnlyDictionary<string, string>? RequiredHeaders,
        DateTimeOffset ExpiresAt,
        long ExpectedByteSize)
    {
        public AuthorizedPackageDownload ToDomain(
            long declaredByteSize,
            string declaredSha256)
        {
            if (!string.Equals(Method, "GET", StringComparison.OrdinalIgnoreCase) ||
                ExpectedByteSize != declaredByteSize ||
                !System.Uri.TryCreate(Uri, UriKind.Absolute, out var parsed))
            {
                throw new InvalidDataException(
                    "Steward returned inconsistent private materialization download authorization.");
            }

            var headers = RequiredHeaders ?? new Dictionary<string, string>();
            ValidateHeaders(headers);
            try
            {
                return new AuthorizedPackageDownload(
                    parsed,
                    headers,
                    ExpiresAt,
                    declaredByteSize,
                    declaredSha256);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    "Steward returned inconsistent private materialization download authorization.",
                    exception);
            }
        }
    }

    private sealed record ReasonDto(string? Reason);
}
