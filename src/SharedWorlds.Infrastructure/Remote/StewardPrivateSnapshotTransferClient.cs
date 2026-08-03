using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Infrastructure.Remote;

public enum RemotePrivateSnapshotUploadStatus
{
    Published,
    AlreadyPublished,
    NotFoundOrUnauthorized,
    InvalidRequest,
    Conflict,
    StorageIntegrityFailure,
    TransferNotFound,
    TransferNotActive,
    TransferExpired,
    IntegrityMismatch,
    PublicationBlocked,
    SnapshotConflict
}

public sealed record RemotePrivateSnapshotUploadResult(
    RemotePrivateSnapshotUploadStatus Status,
    WorldId WorldId,
    RevisionId StateRevisionId,
    RevisionId EnvironmentRevisionId,
    long ByteSize,
    string Sha256,
    Guid? TransferId = null);

public enum RemotePrivateSnapshotDownloadStatus
{
    Authorized,
    NotFoundOrUnauthorized,
    Unavailable,
    AlreadyHere,
    Conflict,
    StorageIntegrityFailure
}

public sealed record RemotePrivateSnapshotDownloadPlan(
    WorldId WorldId,
    string SourceInstallationId,
    RevisionId StateRevisionId,
    RevisionId EnvironmentRevisionId,
    string GameAdapterId,
    long ExpectedByteSize,
    string ExpectedSha256,
    EnvironmentManifest EnvironmentManifest,
    AuthorizedPackageDownload Authorization);

public sealed record RemotePrivateSnapshotDownloadResult(
    RemotePrivateSnapshotDownloadStatus Status,
    RemotePrivateSnapshotDownloadPlan? Plan,
    string? Reason);

public sealed record RemoteVerifiedPrivateSnapshot(
    RemotePrivateSnapshotDownloadPlan Plan,
    VerifiedCachedPackage Package);

/// <summary>
/// Typed authenticated client for owner-private snapshot transfer. API responses are untrusted
/// protocol input: every World/head/adapter/size/hash/manifest/part field is independently checked
/// before direct transfer or verified-cache publication. Signed URLs authorize bytes but never define
/// snapshot identity.
/// </summary>
public sealed class StewardPrivateSnapshotTransferClient
{
    private const int HashBufferBytes = 1024 * 1024;
    private const int MaximumRequiredHeaders = 64;
    private const int MaximumHeaderTextLength = 4096;
    private static readonly TimeSpan DefaultPartUploadTimeout = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _apiClient;
    private readonly HttpClient _transferClient;
    private readonly VerifiedPackageCache _cache;
    private readonly Func<CancellationToken, Task<string?>> _accessTokenProvider;
    private readonly TimeSpan _partUploadTimeout;

    public StewardPrivateSnapshotTransferClient(
        HttpClient apiClient,
        HttpClient transferClient,
        VerifiedPackageCache cache,
        Func<CancellationToken, Task<string?>> accessTokenProvider,
        TimeSpan? partUploadTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(transferClient);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(accessTokenProvider);
        if (apiClient.BaseAddress is null)
        {
            throw new ArgumentException(
                "Steward API HttpClient requires a BaseAddress.",
                nameof(apiClient));
        }

        _ = StewardRemoteEndpointPolicy.NormalizeApiBaseAddress(apiClient.BaseAddress);
        var resolvedTimeout = partUploadTimeout ?? DefaultPartUploadTimeout;
        if (resolvedTimeout <= TimeSpan.Zero ||
            resolvedTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(partUploadTimeout));
        }

        _apiClient = apiClient;
        _transferClient = transferClient;
        _cache = cache;
        _accessTokenProvider = accessTokenProvider;
        _partUploadTimeout = resolvedTimeout;
    }

    public async Task<RemotePrivateSnapshotUploadResult> UploadAsync(
        WorldId worldId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        string gameAdapterId,
        EnvironmentManifest environmentManifest,
        Stream statePackage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environmentManifest);
        ValidateSnapshotIdentity(
            worldId,
            stateRevisionId,
            environmentRevisionId,
            gameAdapterId,
            environmentManifest);
        ArgumentNullException.ThrowIfNull(statePackage);
        if (!statePackage.CanRead || !statePackage.CanSeek)
        {
            throw new ArgumentException(
                "Private snapshot package stream must be readable and seekable for hashing and resume.",
                nameof(statePackage));
        }

        var originalPosition = statePackage.Position;
        var byteSize = statePackage.Length - originalPosition;
        if (byteSize is < 1 or > OwnedWorldSnapshot.MaximumStatePackageByteSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statePackage),
                $"Private snapshot package must contain between 1 and {OwnedWorldSnapshot.MaximumStatePackageByteSize} bytes.");
        }

        var sha256 = await ComputeSha256Async(
            statePackage,
            originalPosition,
            cancellationToken);
        try
        {
            var begin = await BeginUploadAsync(
                worldId,
                stateRevisionId,
                environmentRevisionId,
                gameAdapterId,
                byteSize,
                sha256,
                environmentManifest,
                cancellationToken);
            if (begin.TerminalStatus is { } terminal)
            {
                return Result(
                    terminal,
                    worldId,
                    stateRevisionId,
                    environmentRevisionId,
                    byteSize,
                    sha256);
            }

            var transfer = begin.Transfer
                ?? throw new InvalidDataException(
                    "PrivateSnapshotUploadStarted omitted required transfer metadata.");
            ValidateTransfer(
                transfer,
                worldId,
                stateRevisionId,
                environmentRevisionId,
                gameAdapterId,
                byteSize,
                sha256);

            try
            {
                var progress = await GetProgressAsync(
                    transfer.TransferId,
                    cancellationToken);
                ValidateTransfer(
                    progress,
                    worldId,
                    stateRevisionId,
                    environmentRevisionId,
                    gameAdapterId,
                    byteSize,
                    sha256);
                ValidateCompletedParts(progress);

                if (!progress.ProviderUploadCompleted)
                {
                    var completed = progress.CompletedParts.ToDictionary(part => part.PartNumber);
                    for (var partNumber = 1; partNumber <= progress.PartCount; partNumber++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var expectedPartBytes = ExpectedPartBytes(progress, partNumber);
                        if (completed.ContainsKey(partNumber))
                        {
                            continue;
                        }

                        var authorization = await AuthorizePartAsync(
                            progress.TransferId,
                            partNumber,
                            cancellationToken);
                        ValidatePartAuthorization(authorization, expectedPartBytes);
                        statePackage.Position = originalPosition +
                                                ((long)(partNumber - 1) *
                                                 progress.PartSizeBytes);
                        await UploadPartAsync(
                            statePackage,
                            expectedPartBytes,
                            authorization,
                            cancellationToken);
                    }
                }

                var finalized = await FinalizeAsync(
                    progress.TransferId,
                    worldId,
                    stateRevisionId,
                    environmentRevisionId,
                    gameAdapterId,
                    byteSize,
                    sha256,
                    environmentManifest,
                    cancellationToken);
                return Result(
                    finalized,
                    worldId,
                    stateRevisionId,
                    environmentRevisionId,
                    byteSize,
                    sha256,
                    progress.TransferId);
            }
            catch (StewardPrivateSnapshotTransferStateException exception)
            {
                return Result(
                    exception.Status,
                    worldId,
                    stateRevisionId,
                    environmentRevisionId,
                    byteSize,
                    sha256,
                    transfer.TransferId);
            }
        }
        finally
        {
            statePackage.Position = originalPosition;
        }
    }

    public async Task<RemotePrivateSnapshotDownloadResult> AuthorizeDownloadAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Get,
            $"api/v1/private-worlds/{worldId.Value:D}/snapshot-download",
            cancellationToken);
        var response = await SendApiAsync(
            request,
            "private-snapshot-download",
            cancellationToken);
        switch (response.Code)
        {
            case "PrivateSnapshotDownloadAuthorized":
                RequireStatus(response, HttpStatusCode.OK);
                return new(
                    RemotePrivateSnapshotDownloadStatus.Authorized,
                    DeserializeRequiredData<DownloadPlanDto>(response).ToDomain(worldId),
                    Reason: null);
            case "PrivateSnapshotNotFound":
                RequireStatus(response, HttpStatusCode.NotFound);
                return new(
                    RemotePrivateSnapshotDownloadStatus.NotFoundOrUnauthorized,
                    Plan: null,
                    Reason: null);
            case "PrivateSnapshotUnavailable":
                RequireStatus(response, HttpStatusCode.Conflict);
                return ReasonResult(
                    RemotePrivateSnapshotDownloadStatus.Unavailable,
                    response);
            case "PrivateSnapshotAlreadyHere":
                RequireStatus(response, HttpStatusCode.Conflict);
                return ReasonResult(
                    RemotePrivateSnapshotDownloadStatus.AlreadyHere,
                    response);
            case "PrivateSnapshotHeadConflict":
                RequireStatus(response, HttpStatusCode.Conflict);
                return ReasonResult(
                    RemotePrivateSnapshotDownloadStatus.Conflict,
                    response);
            case "PrivateSnapshotStorageIntegrityFailure":
                RequireStatus(response, HttpStatusCode.Conflict);
                return ReasonResult(
                    RemotePrivateSnapshotDownloadStatus.StorageIntegrityFailure,
                    response);
            default:
                throw CreateUnexpectedResponse(response);
        }
    }

    public async Task<RemoteVerifiedPrivateSnapshot?> EnsureDownloadedAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        var authorization = await AuthorizeDownloadAsync(worldId, cancellationToken);
        if (authorization.Status != RemotePrivateSnapshotDownloadStatus.Authorized)
        {
            return null;
        }

        var plan = authorization.Plan
            ?? throw new InvalidDataException(
                "Private snapshot download authorization omitted its typed plan.");
        var cached = await _cache.EnsureAsync(plan.Authorization, cancellationToken);
        if (cached.ByteSize != plan.ExpectedByteSize ||
            !string.Equals(
                cached.Sha256,
                plan.ExpectedSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageIntegrityException(
                "Verified private snapshot cache result disagrees with the authorized exact identity.");
        }

        return new(plan, cached);
    }

    public async Task<(RemotePrivateSnapshotDownloadPlan Plan, Stream Package)?>
        OpenVerifiedReadAsync(
            WorldId worldId,
            CancellationToken cancellationToken = default)
    {
        var authorization = await AuthorizeDownloadAsync(worldId, cancellationToken);
        if (authorization.Status != RemotePrivateSnapshotDownloadStatus.Authorized)
        {
            return null;
        }

        var plan = authorization.Plan
            ?? throw new InvalidDataException(
                "Private snapshot download authorization omitted its typed plan.");
        var stream = await _cache.OpenVerifiedReadAsync(
            plan.Authorization,
            cancellationToken);
        return (plan, stream);
    }

    private async Task<BeginResult> BeginUploadAsync(
        WorldId worldId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        string gameAdapterId,
        long byteSize,
        string sha256,
        EnvironmentManifest environmentManifest,
        CancellationToken cancellationToken)
    {
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Post,
            $"api/v1/private-worlds/{worldId.Value:D}/snapshot-upload",
            cancellationToken);
        request.Content = JsonContent.Create(new BeginUploadRequest(
            stateRevisionId.Value,
            environmentRevisionId.Value,
            gameAdapterId,
            byteSize,
            sha256,
            environmentManifest));
        var response = await SendApiAsync(
            request,
            "private-snapshot-upload",
            cancellationToken);
        switch (response.Code)
        {
            case "PrivateSnapshotUploadStarted":
                RequireStatus(response, HttpStatusCode.OK);
                return new(
                    DeserializeRequiredData<TransferDto>(response).ToDomain(),
                    TerminalStatus: null);
            case "PrivateSnapshotAlreadyPublished":
                RequireStatus(response, HttpStatusCode.OK);
                return new(
                    Transfer: null,
                    RemotePrivateSnapshotUploadStatus.AlreadyPublished);
            case "PrivateSnapshotNotFound":
                RequireStatus(response, HttpStatusCode.NotFound);
                return new(
                    Transfer: null,
                    RemotePrivateSnapshotUploadStatus.NotFoundOrUnauthorized);
            case "PrivateSnapshotInvalidRequest":
                RequireStatus(response, HttpStatusCode.BadRequest);
                return new(
                    Transfer: null,
                    RemotePrivateSnapshotUploadStatus.InvalidRequest);
            case "PrivateSnapshotConflict":
                RequireStatus(response, HttpStatusCode.Conflict);
                return new(
                    Transfer: null,
                    RemotePrivateSnapshotUploadStatus.Conflict);
            case "PrivateSnapshotStorageIntegrityFailure":
                RequireStatus(response, HttpStatusCode.Conflict);
                return new(
                    Transfer: null,
                    RemotePrivateSnapshotUploadStatus.StorageIntegrityFailure);
            default:
                throw CreateUnexpectedResponse(response);
        }
    }

    private async Task<TransferMetadata> GetProgressAsync(
        Guid transferId,
        CancellationToken cancellationToken)
    {
        ValidateTransferId(transferId);
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Get,
            $"api/v1/private-snapshot-transfers/{transferId:D}",
            cancellationToken);
        var response = await SendApiAsync(
            request,
            "private-snapshot-progress",
            cancellationToken);
        switch (response.Code)
        {
            case "PrivateSnapshotTransferProgress":
                RequireStatus(response, HttpStatusCode.OK);
                return DeserializeRequiredData<TransferDto>(response).ToDomain();
            case "PrivateSnapshotTransferNotFound":
                RequireStatus(response, HttpStatusCode.NotFound);
                throw new StewardPrivateSnapshotTransferStateException(
                    RemotePrivateSnapshotUploadStatus.TransferNotFound,
                    "Private snapshot transfer disappeared before resume.");
            default:
                throw CreateUnexpectedResponse(response);
        }
    }

    private async Task<PartAuthorization> AuthorizePartAsync(
        Guid transferId,
        int partNumber,
        CancellationToken cancellationToken)
    {
        ValidateTransferId(transferId);
        if (partNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(partNumber));
        }

        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Post,
            $"api/v1/private-snapshot-transfers/{transferId:D}/parts/{partNumber}/authorization",
            cancellationToken);
        var response = await SendApiAsync(
            request,
            "private-snapshot-part",
            cancellationToken);
        switch (response.Code)
        {
            case "PrivateSnapshotPartAuthorized":
            {
                RequireStatus(response, HttpStatusCode.OK);
                var envelope = DeserializeRequiredData<PartAuthorizationEnvelopeDto>(response);
                return (envelope.Authorization
                        ?? throw new InvalidDataException(
                            "PrivateSnapshotPartAuthorized omitted required authorization."))
                    .ToDomain();
            }
            case "PrivateSnapshotTransferNotFound":
                RequireStatus(response, HttpStatusCode.NotFound);
                throw new StewardPrivateSnapshotTransferStateException(
                    RemotePrivateSnapshotUploadStatus.TransferNotFound,
                    "Private snapshot transfer disappeared before part authorization.");
            case "PrivateSnapshotTransferNotActive":
                RequireStatus(response, HttpStatusCode.Conflict);
                throw new StewardPrivateSnapshotTransferStateException(
                    RemotePrivateSnapshotUploadStatus.TransferNotActive,
                    "Private snapshot transfer is no longer active.");
            case "PrivateSnapshotTransferExpired":
                RequireStatus(response, HttpStatusCode.Conflict);
                throw new StewardPrivateSnapshotTransferStateException(
                    RemotePrivateSnapshotUploadStatus.TransferExpired,
                    "Private snapshot transfer expired before part authorization.");
            case "PrivateSnapshotPartInvalid":
                RequireStatus(response, HttpStatusCode.BadRequest);
                throw new InvalidDataException(
                    "Backend rejected a part number derived from its own private transfer metadata.");
            default:
                throw CreateUnexpectedResponse(response);
        }
    }

    private async Task<RemotePrivateSnapshotUploadStatus> FinalizeAsync(
        Guid transferId,
        WorldId worldId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        string gameAdapterId,
        long byteSize,
        string sha256,
        EnvironmentManifest environmentManifest,
        CancellationToken cancellationToken)
    {
        ValidateTransferId(transferId);
        using var request = await CreateAuthorizedRequestAsync(
            HttpMethod.Post,
            $"api/v1/private-snapshot-transfers/{transferId:D}/finalize",
            cancellationToken);
        var response = await SendApiAsync(
            request,
            "private-snapshot-finalize",
            cancellationToken);
        switch (response.Code)
        {
            case "PrivateSnapshotFinalized":
            case "PrivateSnapshotAlreadyFinalized":
                RequireStatus(response, HttpStatusCode.OK);
                DeserializeRequiredData<SnapshotDto>(response).ValidateExact(
                    worldId,
                    stateRevisionId,
                    environmentRevisionId,
                    gameAdapterId,
                    byteSize,
                    sha256,
                    environmentManifest);
                return RemotePrivateSnapshotUploadStatus.Published;
            case "PrivateSnapshotTransferNotFound":
                RequireStatus(response, HttpStatusCode.NotFound);
                return RemotePrivateSnapshotUploadStatus.TransferNotFound;
            case "PrivateSnapshotTransferNotActive":
                RequireStatus(response, HttpStatusCode.Conflict);
                return RemotePrivateSnapshotUploadStatus.TransferNotActive;
            case "PrivateSnapshotTransferExpired":
                RequireStatus(response, HttpStatusCode.Conflict);
                return RemotePrivateSnapshotUploadStatus.TransferExpired;
            case "PrivateSnapshotIntegrityMismatch":
                RequireStatus(response, HttpStatusCode.Conflict);
                return RemotePrivateSnapshotUploadStatus.IntegrityMismatch;
            case "PrivateSnapshotPublicationBlocked":
                RequireStatus(response, HttpStatusCode.Conflict);
                return RemotePrivateSnapshotUploadStatus.PublicationBlocked;
            case "PrivateSnapshotConflict":
                RequireStatus(response, HttpStatusCode.Conflict);
                return RemotePrivateSnapshotUploadStatus.SnapshotConflict;
            default:
                throw CreateUnexpectedResponse(response);
        }
    }

    private async Task UploadPartAsync(
        Stream package,
        long expectedBytes,
        PartAuthorization authorization,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, authorization.Uri);
        using var segment = new NonOwningReadSegmentStream(package, expectedBytes);
        request.Content = new StreamContent(segment, 128 * 1024);
        request.Content.Headers.ContentLength = expectedBytes;
        foreach (var header in authorization.RequiredHeaders)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value) &&
                !request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                throw new InvalidOperationException(
                    $"Required private snapshot upload header '{header.Key}' cannot be applied.");
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_partUploadTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _transferClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            timeout.IsCancellationRequested)
        {
            throw new RemotePackagePartUploadTimeoutException(_partUploadTimeout);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new IOException(
                    $"Private snapshot part upload failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }
        }

        if (segment.Remaining != 0)
        {
            throw new EndOfStreamException(
                $"Private snapshot stream ended {segment.Remaining} bytes before the authorized part completed.");
        }
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
                "An authenticated Steward session is required before private snapshot transfer.");
        }

        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<ApiResponse> SendApiAsync(
        HttpRequestMessage request,
        string operation,
        CancellationToken cancellationToken)
    {
        using var response = await _apiClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var envelope = await RemoteApiJson.DeserializeAsync<ApiResponse>(
            response.Content,
            JsonOptions,
            operation,
            cancellationToken);
        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Code))
        {
            throw new InvalidDataException(
                $"Steward returned an invalid {operation} response.");
        }

        return envelope with { StatusCode = response.StatusCode };
    }

    private static T DeserializeRequiredData<T>(ApiResponse response)
    {
        if (response.Data is null ||
            response.Data.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidDataException(
                $"Steward response '{response.Code}' omitted required data.");
        }

        try
        {
            return response.Data.Value.Deserialize<T>(JsonOptions)
                   ?? throw new InvalidDataException(
                       $"Steward response '{response.Code}' returned null required data.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Steward returned invalid private snapshot data for '{response.Code}'.",
                exception);
        }
    }

    private static RemotePrivateSnapshotDownloadResult ReasonResult(
        RemotePrivateSnapshotDownloadStatus status,
        ApiResponse response)
    {
        var reason = DeserializeRequiredData<ReasonDto>(response).Reason;
        ValidateText(
            reason,
            "Private snapshot reason",
            1024,
            allowWhitespace: true);
        return new(status, Plan: null, reason);
    }

    private static void RequireStatus(ApiResponse response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
        {
            throw new InvalidDataException(
                $"Steward response '{response.Code}' used HTTP {(int)response.StatusCode} instead of expected {(int)expected}.");
        }
    }

    private static void ValidateTransfer(
        TransferMetadata transfer,
        WorldId worldId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        string gameAdapterId,
        long byteSize,
        string sha256)
    {
        if (transfer.TransferId == Guid.Empty ||
            transfer.WorldId != worldId ||
            transfer.StateRevisionId != stateRevisionId ||
            transfer.EnvironmentRevisionId != environmentRevisionId ||
            !string.Equals(
                transfer.GameAdapterId,
                gameAdapterId,
                StringComparison.Ordinal) ||
            transfer.ExpectedByteSize != byteSize ||
            !string.Equals(
                transfer.ExpectedSha256,
                sha256,
                StringComparison.OrdinalIgnoreCase) ||
            transfer.PartSizeBytes <= 0 ||
            transfer.PartCount <= 0 ||
            transfer.CreatedAt == default ||
            transfer.ExpiresAt <= transfer.CreatedAt ||
            !Enum.IsDefined(typeof(RemotePrivateSnapshotTransferState), transfer.State))
        {
            throw new InvalidDataException(
                "Steward private transfer metadata does not match the requested exact snapshot.");
        }

        var expectedPartCount = checked((int)(
            (byteSize + transfer.PartSizeBytes - 1L) /
            transfer.PartSizeBytes));
        if (transfer.PartCount != expectedPartCount)
        {
            throw new InvalidDataException(
                "Steward private transfer part count is inconsistent with package size.");
        }
    }

    private static void ValidateCompletedParts(TransferMetadata transfer)
    {
        var seen = new HashSet<int>();
        foreach (var part in transfer.CompletedParts)
        {
            if (!seen.Add(part.PartNumber) ||
                part.PartNumber < 1 ||
                part.PartNumber > transfer.PartCount ||
                part.ByteSize != ExpectedPartBytes(transfer, part.PartNumber))
            {
                throw new InvalidDataException(
                    "Steward returned invalid completed-part evidence for a private snapshot transfer.");
            }
        }
    }

    private static long ExpectedPartBytes(TransferMetadata transfer, int partNumber)
    {
        if (partNumber < 1 || partNumber > transfer.PartCount)
        {
            throw new ArgumentOutOfRangeException(nameof(partNumber));
        }

        var offset = (long)(partNumber - 1) * transfer.PartSizeBytes;
        return Math.Min(transfer.PartSizeBytes, transfer.ExpectedByteSize - offset);
    }

    private static void ValidatePartAuthorization(
        PartAuthorization authorization,
        long expectedPartBytes)
    {
        if (!string.Equals(authorization.Method, "PUT", StringComparison.OrdinalIgnoreCase) ||
            authorization.ExpectedByteSize != expectedPartBytes ||
            authorization.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException(
                "Steward returned inconsistent private snapshot part authorization.");
        }

        ValidateHeaders(authorization.RequiredHeaders);
    }

    private static void ValidateSnapshotIdentity(
        WorldId worldId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        string gameAdapterId,
        EnvironmentManifest environmentManifest)
    {
        ValidateWorldId(worldId);
        if (stateRevisionId.Value == Guid.Empty ||
            environmentRevisionId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Private snapshot state and environment revision IDs are required.");
        }

        new OwnedWorldSnapshot(
            worldId,
            "client-validation",
            "client-validation",
            "client-validation",
            stateRevisionId,
            environmentRevisionId,
            gameAdapterId,
            "client-validation",
            1,
            new string('0', 64),
            environmentManifest,
            DateTimeOffset.UtcNow).Validate();
    }

    private static void ValidateWorldId(WorldId worldId)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }
    }

    private static void ValidateTransferId(Guid transferId)
    {
        if (transferId == Guid.Empty)
        {
            throw new InvalidDataException(
                "Steward returned an empty private snapshot transfer ID.");
        }
    }

    private static void ValidateHeaders(IReadOnlyDictionary<string, string> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (headers.Count > MaximumRequiredHeaders)
        {
            throw new InvalidDataException(
                $"Private snapshot authorization returned more than {MaximumRequiredHeaders} required headers.");
        }

        foreach (var header in headers)
        {
            ValidateText(header.Key, "Transfer header name", 256);
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

    private static bool ManifestEquals(
        EnvironmentManifest? left,
        EnvironmentManifest right)
        => left is not null &&
           JsonNode.DeepEquals(
               JsonSerializer.SerializeToNode(left, JsonOptions),
               JsonSerializer.SerializeToNode(right, JsonOptions));

    private static async Task<string> ComputeSha256Async(
        Stream package,
        long originalPosition,
        CancellationToken cancellationToken)
    {
        package.Position = originalPosition;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[HashBufferBytes];
            while (true)
            {
                var read = await package.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            package.Position = originalPosition;
        }
    }

    private static RemotePrivateSnapshotUploadResult Result(
        RemotePrivateSnapshotUploadStatus status,
        WorldId worldId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        long byteSize,
        string sha256,
        Guid? transferId = null)
        => new(
            status,
            worldId,
            stateRevisionId,
            environmentRevisionId,
            byteSize,
            sha256,
            transferId);

    private static StewardRemoteApiException CreateUnexpectedResponse(ApiResponse response)
        => new(
            response.StatusCode,
            response.Code,
            response.Retryable ||
            response.StatusCode == HttpStatusCode.RequestTimeout ||
            response.StatusCode == HttpStatusCode.TooManyRequests ||
            (int)response.StatusCode >= 500);

    private sealed record BeginResult(
        TransferMetadata? Transfer,
        RemotePrivateSnapshotUploadStatus? TerminalStatus);

    private enum RemotePrivateSnapshotTransferState
    {
        Provisioning,
        Active,
        IntegrityFailed,
        PublicationBlocked,
        SnapshotConflict,
        Finalized
    }

    private sealed record TransferMetadata(
        Guid TransferId,
        WorldId WorldId,
        RevisionId StateRevisionId,
        RevisionId EnvironmentRevisionId,
        string GameAdapterId,
        long ExpectedByteSize,
        string ExpectedSha256,
        int PartSizeBytes,
        int PartCount,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        int State,
        IReadOnlyList<CompletedPart> CompletedParts,
        bool ProviderUploadCompleted);

    private sealed record CompletedPart(int PartNumber, long ByteSize);

    private sealed record PartAuthorization(
        Uri Uri,
        string Method,
        IReadOnlyDictionary<string, string> RequiredHeaders,
        DateTimeOffset ExpiresAt,
        long ExpectedByteSize);

    private sealed record BeginUploadRequest(
        Guid StateRevisionId,
        Guid EnvironmentRevisionId,
        string GameAdapterId,
        long ExpectedByteSize,
        string ExpectedSha256,
        EnvironmentManifest EnvironmentManifest);

    private sealed record ApiResponse(
        string Code,
        JsonElement? Data,
        bool Retryable)
    {
        public HttpStatusCode StatusCode { get; init; }
    }

    private sealed record TransferDto(
        Guid TransferId,
        Guid WorldId,
        Guid StateRevisionId,
        Guid EnvironmentRevisionId,
        string GameAdapterId,
        long ExpectedByteSize,
        string ExpectedSha256,
        int PartSizeBytes,
        int PartCount,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        int State,
        IReadOnlyList<CompletedPartDto>? CompletedParts,
        bool ProviderUploadCompleted)
    {
        public TransferMetadata ToDomain()
            => new(
                TransferId,
                new WorldId(WorldId),
                new RevisionId(StateRevisionId),
                new RevisionId(EnvironmentRevisionId),
                GameAdapterId,
                ExpectedByteSize,
                ExpectedSha256,
                PartSizeBytes,
                PartCount,
                CreatedAt,
                ExpiresAt,
                State,
                (CompletedParts ?? Array.Empty<CompletedPartDto>())
                    .Select(static part => part.ToDomain())
                    .ToArray(),
                ProviderUploadCompleted);
    }

    private sealed record CompletedPartDto(int PartNumber, long ByteSize)
    {
        public CompletedPart ToDomain() => new(PartNumber, ByteSize);
    }

    private sealed record PartAuthorizationEnvelopeDto(
        PartAuthorizationDto? Authorization);

    private sealed record PartAuthorizationDto(
        string? Uri,
        string? Method,
        IReadOnlyDictionary<string, string>? RequiredHeaders,
        DateTimeOffset ExpiresAt,
        long ExpectedByteSize)
    {
        public PartAuthorization ToDomain()
        {
            if (!System.Uri.TryCreate(Uri, UriKind.Absolute, out var parsed) ||
                !StewardRemoteEndpointPolicy.IsAllowedHttpEndpoint(parsed))
            {
                throw new InvalidDataException(
                    "Steward returned an invalid private snapshot object-storage URI.");
            }

            var headers = RequiredHeaders ?? new Dictionary<string, string>();
            ValidateHeaders(headers);
            return new(
                parsed,
                Method ?? string.Empty,
                headers,
                ExpiresAt,
                ExpectedByteSize);
        }
    }

    private sealed record SnapshotDto(
        Guid WorldId,
        string? SourceInstallationId,
        Guid StateRevisionId,
        Guid EnvironmentRevisionId,
        string? GameAdapterId,
        long ExpectedByteSize,
        string? ExpectedSha256,
        EnvironmentManifest? EnvironmentManifest,
        DateTimeOffset PublishedAt)
    {
        public void ValidateExact(
            WorldId expectedWorldId,
            RevisionId expectedStateRevisionId,
            RevisionId expectedEnvironmentRevisionId,
            string expectedGameAdapterId,
            long expectedByteSize,
            string expectedSha256,
            EnvironmentManifest expectedManifest)
        {
            ValidateText(
                SourceInstallationId,
                "Source installation ID",
                OwnedWorldSnapshot.MaximumInstallationIdLength,
                allowWhitespace: true);
            if (WorldId != expectedWorldId.Value ||
                StateRevisionId != expectedStateRevisionId.Value ||
                EnvironmentRevisionId != expectedEnvironmentRevisionId.Value ||
                !string.Equals(
                    GameAdapterId,
                    expectedGameAdapterId,
                    StringComparison.Ordinal) ||
                ExpectedByteSize != expectedByteSize ||
                !string.Equals(
                    ExpectedSha256,
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                PublishedAt == default ||
                !ManifestEquals(EnvironmentManifest, expectedManifest))
            {
                throw new InvalidDataException(
                    "Steward finalized a private snapshot with metadata that differs from the requested exact identity.");
            }

            ValidateSnapshotIdentity(
                expectedWorldId,
                expectedStateRevisionId,
                expectedEnvironmentRevisionId,
                expectedGameAdapterId,
                EnvironmentManifest!);
        }
    }

    private sealed record DownloadPlanDto(
        Guid WorldId,
        string? SourceInstallationId,
        Guid StateRevisionId,
        Guid EnvironmentRevisionId,
        string? GameAdapterId,
        long ExpectedByteSize,
        string? ExpectedSha256,
        EnvironmentManifest? EnvironmentManifest,
        DownloadAuthorizationDto? Authorization)
    {
        public RemotePrivateSnapshotDownloadPlan ToDomain(WorldId requestedWorldId)
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
                Authorization is null)
            {
                throw new InvalidDataException(
                    "Steward returned a private snapshot plan for the wrong or invalid exact identity.");
            }

            var stateId = new RevisionId(StateRevisionId);
            var environmentId = new RevisionId(EnvironmentRevisionId);
            ValidateSnapshotIdentity(
                requestedWorldId,
                stateId,
                environmentId,
                GameAdapterId ?? string.Empty,
                EnvironmentManifest);
            var authorization = Authorization.ToDomain(
                ExpectedByteSize,
                ExpectedSha256 ?? string.Empty);
            return new(
                requestedWorldId,
                SourceInstallationId!,
                stateId,
                environmentId,
                GameAdapterId!,
                ExpectedByteSize,
                authorization.ExpectedSha256,
                EnvironmentManifest,
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
                    "Steward returned inconsistent private snapshot download authorization.");
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
                    "Steward returned inconsistent private snapshot download authorization.",
                    exception);
            }
        }
    }

    private sealed record ReasonDto(string? Reason);

    private sealed class NonOwningReadSegmentStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public NonOwningReadSegmentStream(Stream inner, long length)
        {
            ArgumentNullException.ThrowIfNull(inner);
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            _inner = inner;
            _remaining = length;
        }

        public long Remaining => _remaining;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var allowed = (int)Math.Min(count, _remaining);
            if (allowed == 0)
            {
                return 0;
            }

            var read = _inner.Read(buffer, offset, allowed);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var allowed = (int)Math.Min(buffer.Length, _remaining);
            if (allowed == 0)
            {
                return 0;
            }

            var read = await _inner.ReadAsync(buffer[..allowed], cancellationToken);
            _remaining -= read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}

public sealed class StewardPrivateSnapshotTransferStateException : IOException
{
    public StewardPrivateSnapshotTransferStateException(
        RemotePrivateSnapshotUploadStatus status,
        string message)
        : base(message)
    {
        Status = status;
    }

    public RemotePrivateSnapshotUploadStatus Status { get; }
}
