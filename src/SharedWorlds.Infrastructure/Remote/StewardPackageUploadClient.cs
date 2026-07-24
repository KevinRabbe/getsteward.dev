using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Infrastructure.Remote;

public enum RemotePackageUploadStatus
{
    Published,
    AlreadyPublished,
    WorldNotFoundOrUnauthorized,
    InvalidRequest,
    RequiredEnvironmentMissing,
    RevisionConflict,
    StorageIntegrityFailure,
    TransferNotFound,
    TransferNotActive,
    TransferExpired,
    IntegrityMismatch,
    PublicationConflict,
    PublicationBlocked
}

public sealed record RemotePackageUploadResult(
    RemotePackageUploadStatus Status,
    RevisionId RevisionId,
    long ByteSize,
    string Sha256,
    Guid? TransferId = null);

/// <summary>
/// Resumable desktop multipart publisher for immutable Steward packages. Steward authorizes the
/// transfer; package bytes move directly between the desktop and object storage. Re-running the
/// method after interruption safely resumes from backend-observed completed parts.
/// </summary>
public sealed class StewardPackageUploadClient
{
    private const int HashBufferBytes = 1024 * 1024;
    private static readonly TimeSpan DefaultPartUploadTimeout = TimeSpan.FromMinutes(30);

    private readonly HttpClient _apiClient;
    private readonly HttpClient _transferClient;
    private readonly TimeSpan _partUploadTimeout;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public StewardPackageUploadClient(
        HttpClient apiClient,
        HttpClient transferClient,
        TimeSpan? partUploadTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(transferClient);
        if (apiClient.BaseAddress is null)
        {
            throw new ArgumentException("Steward API HttpClient requires a BaseAddress.", nameof(apiClient));
        }

        var resolvedPartUploadTimeout = partUploadTimeout ?? DefaultPartUploadTimeout;
        if (resolvedPartUploadTimeout <= TimeSpan.Zero || resolvedPartUploadTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(partUploadTimeout));
        }

        _apiClient = apiClient;
        _transferClient = transferClient;
        _partUploadTimeout = resolvedPartUploadTimeout;
    }

    public async Task<RemotePackageUploadResult> UploadAsync(
        WorldId worldId,
        RevisionId revisionId,
        RemotePackageKind kind,
        Stream package,
        RevisionId? requiredEnvironmentRevisionId,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        Validate(worldId, revisionId, kind, package, requiredEnvironmentRevisionId, accessToken);

        var originalPosition = package.Position;
        var byteSize = package.Length - originalPosition;
        if (byteSize <= 0)
        {
            throw new ArgumentException("Package stream must contain at least one byte.", nameof(package));
        }

        var sha256 = await ComputeSha256Async(package, originalPosition, cancellationToken);
        package.Position = originalPosition;

        var begin = await BeginAsync(
            worldId,
            revisionId,
            kind,
            byteSize,
            sha256,
            requiredEnvironmentRevisionId,
            accessToken,
            cancellationToken);

        if (begin.TerminalStatus is { } terminal)
        {
            return new RemotePackageUploadResult(terminal, revisionId, byteSize, sha256);
        }

        var transfer = begin.Transfer
            ?? throw new InvalidDataException("TransferStarted omitted transfer metadata.");
        ValidateTransfer(transfer, worldId, revisionId, kind, byteSize, sha256);

        var progress = await GetProgressAsync(transfer.TransferId, accessToken, cancellationToken);
        ValidateTransfer(progress.Transfer, worldId, revisionId, kind, byteSize, sha256);

        if (!progress.ProviderUploadCompleted)
        {
            var completed = progress.CompletedParts.ToDictionary(part => part.PartNumber);
            for (var partNumber = 1; partNumber <= transfer.PartCount; partNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expectedPartBytes = ExpectedPartBytes(transfer, partNumber);
                if (completed.TryGetValue(partNumber, out var existing))
                {
                    if (existing.ByteSize != expectedPartBytes)
                    {
                        throw new InvalidDataException(
                            $"Backend reported {existing.ByteSize} bytes for part {partNumber}, expected {expectedPartBytes}.");
                    }

                    continue;
                }

                var authorization = await AuthorizePartAsync(
                    transfer.TransferId,
                    partNumber,
                    accessToken,
                    cancellationToken);
                if (!string.Equals(authorization.Method, "PUT", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Steward authorized unsupported object-storage method '{authorization.Method}'.");
                }

                if (authorization.ExpectedByteSize != expectedPartBytes)
                {
                    throw new InvalidDataException(
                        $"Part authorization expected {authorization.ExpectedByteSize} bytes, expected {expectedPartBytes}.");
                }

                if (authorization.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    throw new IOException("Object-storage part authorization expired before upload.");
                }

                var partOffset = originalPosition + ((long)(partNumber - 1) * transfer.PartSizeBytes);
                package.Position = partOffset;
                await UploadPartAsync(package, expectedPartBytes, authorization, cancellationToken);
            }
        }

        var finalized = await FinalizeAsync(transfer.TransferId, accessToken, cancellationToken);
        return new RemotePackageUploadResult(
            finalized,
            revisionId,
            byteSize,
            sha256,
            transfer.TransferId);
    }

    private async Task<BeginResult> BeginAsync(
        WorldId worldId,
        RevisionId revisionId,
        RemotePackageKind kind,
        long byteSize,
        string sha256,
        RevisionId? requiredEnvironmentRevisionId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"api/v1/worlds/{worldId.Value:D}/transfers",
            accessToken);
        request.Content = JsonContent.Create(new BeginRequest(
            revisionId.Value,
            kind.ToString(),
            byteSize,
            sha256,
            requiredEnvironmentRevisionId?.Value));
        var response = await SendApiAsync(request, cancellationToken);
        return response.Code switch
        {
            "TransferStarted" => new(
                DeserializeRequiredData<TransferDto>(response).ToDomain(),
                null),
            "AlreadyPublished" => new(null, RemotePackageUploadStatus.AlreadyPublished),
            "WorldNotFoundOrUnauthorized" => new(null, RemotePackageUploadStatus.WorldNotFoundOrUnauthorized),
            "InvalidTransferRequest" => new(null, RemotePackageUploadStatus.InvalidRequest),
            "RequiredEnvironmentMissing" => new(null, RemotePackageUploadStatus.RequiredEnvironmentMissing),
            "RevisionConflict" => new(null, RemotePackageUploadStatus.RevisionConflict),
            "StorageIntegrityFailure" => new(null, RemotePackageUploadStatus.StorageIntegrityFailure),
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    private async Task<TransferProgress> GetProgressAsync(
        Guid transferId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(
            HttpMethod.Get,
            $"api/v1/transfers/{transferId:D}",
            accessToken);
        var response = await SendApiAsync(request, cancellationToken);
        return response.Code switch
        {
            "TransferProgress" => DeserializeRequiredData<ProgressDto>(response).ToDomain(),
            "TransferNotFound" => throw new StewardPackageUploadStateException(
                RemotePackageUploadStatus.TransferNotFound,
                "Transfer disappeared before upload could resume."),
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    private async Task<PartAuthorization> AuthorizePartAsync(
        Guid transferId,
        int partNumber,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"api/v1/transfers/{transferId:D}/parts/{partNumber}/authorization",
            accessToken);
        var response = await SendApiAsync(request, cancellationToken);
        return response.Code switch
        {
            "PartAuthorized" => DeserializeRequiredData<PartAuthorizationDto>(response).ToDomain(),
            "TransferNotFound" => throw new StewardPackageUploadStateException(
                RemotePackageUploadStatus.TransferNotFound,
                "Transfer disappeared before part authorization."),
            "TransferNotActive" => throw new StewardPackageUploadStateException(
                RemotePackageUploadStatus.TransferNotActive,
                "Transfer is no longer active."),
            "TransferExpired" => throw new StewardPackageUploadStateException(
                RemotePackageUploadStatus.TransferExpired,
                "Transfer expired before part authorization."),
            "InvalidPart" => throw new InvalidDataException("Backend rejected a part number from its own transfer metadata."),
            _ => throw CreateUnexpectedResponse(response)
        };
    }

    private async Task<RemotePackageUploadStatus> FinalizeAsync(
        Guid transferId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = CreateAuthorizedRequest(
            HttpMethod.Post,
            $"api/v1/transfers/{transferId:D}/finalize",
            accessToken);
        var response = await SendApiAsync(request, cancellationToken);
        return response.Code switch
        {
            "TransferFinalized" or "AlreadyFinalized" => RemotePackageUploadStatus.Published,
            "TransferNotFound" => RemotePackageUploadStatus.TransferNotFound,
            "TransferNotActive" => RemotePackageUploadStatus.TransferNotActive,
            "TransferExpired" => RemotePackageUploadStatus.TransferExpired,
            "IntegrityMismatch" => RemotePackageUploadStatus.IntegrityMismatch,
            "PublicationConflict" => RemotePackageUploadStatus.PublicationConflict,
            "PublicationBlocked" => RemotePackageUploadStatus.PublicationBlocked,
            _ => throw CreateUnexpectedResponse(response)
        };
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
                    $"Required object-storage upload header '{header.Key}' cannot be applied to the PUT request.");
            }
        }

        using var partCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        partCancellation.CancelAfter(_partUploadTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _transferClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                partCancellation.Token);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            partCancellation.IsCancellationRequested)
        {
            throw new RemotePackagePartUploadTimeoutException(_partUploadTimeout);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new IOException(
                    $"Object-storage part upload failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).");
            }
        }

        if (segment.Remaining != 0)
        {
            throw new EndOfStreamException(
                $"Package stream ended {segment.Remaining} bytes before the authorized part was complete.");
        }
    }

    private async Task<ApiResponse> SendApiAsync(
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
            "upload",
            cancellationToken);

        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Code))
        {
            throw new InvalidDataException("Steward returned an invalid upload response.");
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
                $"Steward returned invalid upload data for '{response.Code}'.",
                exception);
        }
    }

    private static async Task<string> ComputeSha256Async(
        Stream package,
        long originalPosition,
        CancellationToken cancellationToken)
    {
        package.Position = originalPosition;
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

    private static long ExpectedPartBytes(TransferMetadata transfer, int partNumber)
    {
        if (partNumber < 1 || partNumber > transfer.PartCount)
        {
            throw new ArgumentOutOfRangeException(nameof(partNumber));
        }

        var offset = (long)(partNumber - 1) * transfer.PartSizeBytes;
        return Math.Min(transfer.PartSizeBytes, transfer.ExpectedByteSize - offset);
    }

    private static void ValidateTransfer(
        TransferMetadata transfer,
        WorldId worldId,
        RevisionId revisionId,
        RemotePackageKind kind,
        long byteSize,
        string sha256)
    {
        if (transfer.TransferId == Guid.Empty ||
            transfer.WorldId != worldId ||
            transfer.RevisionId != revisionId ||
            transfer.Kind != kind ||
            transfer.ExpectedByteSize != byteSize ||
            !string.Equals(transfer.ExpectedSha256, sha256, StringComparison.OrdinalIgnoreCase) ||
            transfer.PartSizeBytes <= 0 ||
            transfer.PartCount <= 0)
        {
            throw new InvalidDataException("Steward transfer metadata does not match the requested immutable package.");
        }

        var expectedPartCount = checked((int)((byteSize + transfer.PartSizeBytes - 1) / transfer.PartSizeBytes));
        if (expectedPartCount != transfer.PartCount)
        {
            throw new InvalidDataException("Steward transfer part count is inconsistent with package size.");
        }
    }

    private static void Validate(
        WorldId worldId,
        RevisionId revisionId,
        RemotePackageKind kind,
        Stream package,
        RevisionId? requiredEnvironmentRevisionId,
        string accessToken)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }

        if (revisionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Revision ID is required.", nameof(revisionId));
        }

        if (kind is not (RemotePackageKind.State or RemotePackageKind.Environment))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentNullException.ThrowIfNull(package);
        if (!package.CanRead || !package.CanSeek)
        {
            throw new ArgumentException("Package stream must be readable and seekable for hashing and multipart resume.", nameof(package));
        }

        if (kind == RemotePackageKind.Environment && requiredEnvironmentRevisionId is not null)
        {
            throw new ArgumentException(
                "Environment packages cannot require another environment revision.",
                nameof(requiredEnvironmentRevisionId));
        }

        if (requiredEnvironmentRevisionId is { Value: var required } && required == Guid.Empty)
        {
            throw new ArgumentException("Required environment revision cannot be empty.", nameof(requiredEnvironmentRevisionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        if (accessToken.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Access token must not contain whitespace.", nameof(accessToken));
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
            response.Retryable || response.StatusCode == HttpStatusCode.RequestTimeout ||
            response.StatusCode == HttpStatusCode.TooManyRequests ||
            (int)response.StatusCode >= 500);

    private sealed record BeginResult(
        TransferMetadata? Transfer,
        RemotePackageUploadStatus? TerminalStatus);

    private sealed record TransferMetadata(
        Guid TransferId,
        WorldId WorldId,
        RevisionId RevisionId,
        RemotePackageKind Kind,
        long ExpectedByteSize,
        string ExpectedSha256,
        RevisionId? RequiredEnvironmentRevisionId,
        int PartSizeBytes,
        int PartCount,
        DateTimeOffset ExpiresAt,
        string State);

    private sealed record CompletedPart(int PartNumber, long ByteSize);

    private sealed record TransferProgress(
        TransferMetadata Transfer,
        IReadOnlyList<CompletedPart> CompletedParts,
        bool ProviderUploadCompleted);

    private sealed record PartAuthorization(
        Uri Uri,
        string Method,
        IReadOnlyDictionary<string, string> RequiredHeaders,
        DateTimeOffset ExpiresAt,
        long ExpectedByteSize);

    private sealed record BeginRequest(
        Guid RevisionId,
        string Kind,
        long ExpectedByteSize,
        string ExpectedSha256,
        Guid? RequiredEnvironmentRevisionId);

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
        Guid RevisionId,
        string Kind,
        long ExpectedByteSize,
        string ExpectedSha256,
        Guid? RequiredEnvironmentRevisionId,
        int PartSizeBytes,
        int PartCount,
        DateTimeOffset ExpiresAt,
        string State)
    {
        public TransferMetadata ToDomain()
        {
            if (!Enum.TryParse<RemotePackageKind>(Kind, ignoreCase: true, out var kind))
            {
                throw new InvalidDataException($"Unknown remote package kind '{Kind}'.");
            }

            return new(
                TransferId,
                new WorldId(WorldId),
                new RevisionId(RevisionId),
                kind,
                ExpectedByteSize,
                ExpectedSha256,
                RequiredEnvironmentRevisionId is { } environment
                    ? new RevisionId(environment)
                    : null,
                PartSizeBytes,
                PartCount,
                ExpiresAt,
                State);
        }
    }

    private sealed record CompletedPartDto(int PartNumber, long ByteSize)
    {
        public CompletedPart ToDomain() => new(PartNumber, ByteSize);
    }

    private sealed record ProgressDto(
        TransferDto Transfer,
        IReadOnlyList<CompletedPartDto> CompletedParts,
        bool ProviderUploadCompleted)
    {
        public TransferProgress ToDomain()
            => new(
                Transfer.ToDomain(),
                CompletedParts.Select(static part => part.ToDomain()).ToArray(),
                ProviderUploadCompleted);
    }

    private sealed record PartAuthorizationDto(
        string Uri,
        string Method,
        IReadOnlyDictionary<string, string> RequiredHeaders,
        DateTimeOffset ExpiresAt,
        long ExpectedByteSize)
    {
        public PartAuthorization ToDomain()
        {
            if (!System.Uri.TryCreate(Uri, UriKind.Absolute, out var parsed) ||
                parsed.Scheme is not ("http" or "https"))
            {
                throw new InvalidDataException("Steward returned an invalid object-storage part URI.");
            }

            return new(parsed, Method, RequiredHeaders, ExpiresAt, ExpectedByteSize);
        }
    }

    private sealed class NonOwningReadSegmentStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public NonOwningReadSegmentStream(Stream inner, long length)
        {
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
            // Deliberately do not dispose the caller-owned package stream.
            base.Dispose(disposing);
        }
    }
}

public sealed class StewardPackageUploadStateException : IOException
{
    public StewardPackageUploadStateException(RemotePackageUploadStatus status, string message)
        : base(message)
    {
        Status = status;
    }

    public RemotePackageUploadStatus Status { get; }
}

public sealed class RemotePackagePartUploadTimeoutException : IOException
{
    public RemotePackagePartUploadTimeoutException(TimeSpan partUploadTimeout)
        : base($"Object-storage package part did not complete within {partUploadTimeout}.")
    {
        PartUploadTimeout = partUploadTimeout;
    }

    public TimeSpan PartUploadTimeout { get; }
}
