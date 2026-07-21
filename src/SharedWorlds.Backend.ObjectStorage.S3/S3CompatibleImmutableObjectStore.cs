using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using SharedWorlds.Backend.Transfers;

namespace SharedWorlds.Backend.ObjectStorage.S3;

public sealed class S3CompatibleImmutableObjectStore : IPrivateImmutableObjectStore, IDisposable
{
    private const string HandlePrefix = "s3mp1_";
    private const int MaximumEncodedHandleLength = 131_072;
    private const int HashBufferBytes = 1024 * 1024;

    private readonly IAmazonS3 _client;
    private readonly string _bucketName;
    private readonly bool _ownsClient;
    private bool _disposed;

    public S3CompatibleImmutableObjectStore(
        IAmazonS3 client,
        string bucketName,
        bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        _client = client;
        _bucketName = bucketName;
        _ownsClient = ownsClient;
    }

    public async Task<ImmutableUploadSession> BeginMultipartUploadAsync(
        string objectKey,
        long expectedByteSize,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateObjectKey(objectKey);
        ValidateExpectedObject(expectedByteSize, expectedSha256);

        var response = await _client.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest
            {
                BucketName = _bucketName,
                Key = objectKey
            },
            cancellationToken);

        if (string.IsNullOrWhiteSpace(response.UploadId))
        {
            throw new InvalidOperationException("S3-compatible storage returned an empty multipart upload ID.");
        }

        var handle = EncodeHandle(new MultipartHandle(
            objectKey,
            response.UploadId,
            expectedByteSize,
            NormalizeSha256(expectedSha256)));
        return new ImmutableUploadSession(handle, objectKey);
    }

    public async Task<ImmutableUploadSnapshot?> GetMultipartUploadAsync(
        string providerUploadId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var handle = DecodeHandle(providerUploadId);
        try
        {
            var parts = await ListPartsAsync(handle, cancellationToken);
            return new ImmutableUploadSnapshot(
                providerUploadId,
                handle.ObjectKey,
                parts
                    .Select(part => new ImmutableUploadedPart(part.PartNumber, part.ByteSize))
                    .ToArray(),
                IsCompleted: false);
        }
        catch (AmazonS3Exception exception) when (IsNoSuchUpload(exception))
        {
            var stored = await InspectObjectAsync(handle.ObjectKey, cancellationToken);
            if (stored is null || !MatchesHandle(stored, handle))
            {
                return null;
            }

            return new ImmutableUploadSnapshot(
                providerUploadId,
                handle.ObjectKey,
                Array.Empty<ImmutableUploadedPart>(),
                IsCompleted: true);
        }
    }

    public async Task<DirectObjectTransferAuthorization> AuthorizeUploadPartAsync(
        string providerUploadId,
        int partNumber,
        long expectedByteSize,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var handle = DecodeHandle(providerUploadId);
        if (partNumber is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(partNumber));
        }

        if (expectedByteSize <= 0 || expectedByteSize > handle.ExpectedByteSize)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedByteSize));
        }

        ValidateFutureExpiry(expiresAt);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = handle.ObjectKey,
            Verb = HttpVerb.PUT,
            Expires = expiresAt.UtcDateTime,
            UploadId = handle.NativeUploadId,
            PartNumber = partNumber
        };
        var url = await _client.GetPreSignedURLAsync(request);
        cancellationToken.ThrowIfCancellationRequested();

        return new DirectObjectTransferAuthorization(
            new Uri(url, UriKind.Absolute),
            "PUT",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Length"] = expectedByteSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            expiresAt,
            expectedByteSize);
    }

    public async Task<ImmutableStoredObject> CompleteMultipartUploadAsync(
        string providerUploadId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var handle = DecodeHandle(providerUploadId);
        IReadOnlyList<CompletedPart> parts;
        try
        {
            parts = await ListPartsAsync(handle, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (IsNoSuchUpload(exception))
        {
            return await RequireCompletedObjectAsync(handle, cancellationToken);
        }

        if (parts.Count == 0)
        {
            throw new InvalidOperationException("Multipart upload has no uploaded parts.");
        }

        ValidateConsecutiveParts(parts);
        var request = new CompleteMultipartUploadRequest
        {
            BucketName = _bucketName,
            Key = handle.ObjectKey,
            UploadId = handle.NativeUploadId,
            MpuObjectSize = handle.ExpectedByteSize
        };
        request.AddPartETags(parts.Select(part => new PartETag(part.PartNumber, part.ETag)));

        try
        {
            await _client.CompleteMultipartUploadAsync(request, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (IsNoSuchUpload(exception))
        {
            return await RequireCompletedObjectAsync(handle, cancellationToken);
        }

        return await RequireCompletedObjectAsync(handle, cancellationToken);
    }

    public async Task AbortMultipartUploadAsync(
        string providerUploadId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var handle = DecodeHandle(providerUploadId);
        try
        {
            await _client.AbortMultipartUploadAsync(
                new AbortMultipartUploadRequest
                {
                    BucketName = _bucketName,
                    Key = handle.ObjectKey,
                    UploadId = handle.NativeUploadId
                },
                cancellationToken);
        }
        catch (AmazonS3Exception exception) when (IsNoSuchUpload(exception))
        {
            // Idempotent abort: the upload is already absent.
        }
    }

    public async Task<ImmutableStoredObject?> InspectObjectAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateObjectKey(objectKey);
        GetObjectResponse response;
        try
        {
            response = await _client.GetObjectAsync(_bucketName, objectKey, cancellationToken);
        }
        catch (AmazonS3Exception exception) when (IsObjectNotFound(exception))
        {
            return null;
        }

        using (response)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(HashBufferBytes);
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long byteSize = 0;
                while (true)
                {
                    var read = await response.ResponseStream.ReadAsync(
                        buffer.AsMemory(0, buffer.Length),
                        cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                    byteSize = checked(byteSize + read);
                }

                return new ImmutableStoredObject(
                    objectKey,
                    byteSize,
                    Convert.ToHexString(hash.GetHashAndReset()));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    public async Task<DirectObjectTransferAuthorization> AuthorizeDownloadAsync(
        string objectKey,
        long expectedByteSize,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateObjectKey(objectKey);
        if (expectedByteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedByteSize));
        }

        ValidateFutureExpiry(expiresAt);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Expires = expiresAt.UtcDateTime
        };
        var url = await _client.GetPreSignedURLAsync(request);
        cancellationToken.ThrowIfCancellationRequested();
        return new DirectObjectTransferAuthorization(
            new Uri(url, UriKind.Absolute),
            "GET",
            new Dictionary<string, string>(),
            expiresAt,
            expectedByteSize);
    }

    public async Task DeleteObjectAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateObjectKey(objectKey);
        await _client.DeleteObjectAsync(_bucketName, objectKey, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private async Task<IReadOnlyList<CompletedPart>> ListPartsAsync(
        MultipartHandle handle,
        CancellationToken cancellationToken)
    {
        var parts = new List<CompletedPart>();
        string? marker = null;
        while (true)
        {
            var response = await _client.ListPartsAsync(
                new ListPartsRequest
                {
                    BucketName = _bucketName,
                    Key = handle.ObjectKey,
                    UploadId = handle.NativeUploadId,
                    PartNumberMarker = marker
                },
                cancellationToken);

            foreach (var part in response.Parts ?? [])
            {
                if (part.PartNumber is not { } partNumber ||
                    part.Size is not { } byteSize ||
                    string.IsNullOrWhiteSpace(part.ETag))
                {
                    throw new InvalidOperationException("S3-compatible storage returned incomplete multipart part metadata.");
                }

                parts.Add(new CompletedPart(partNumber, byteSize, part.ETag));
            }

            if (response.IsTruncated != true)
            {
                break;
            }

            if (response.NextPartNumberMarker is not { } nextMarker || nextMarker <= 0)
            {
                throw new InvalidOperationException("S3-compatible storage returned truncated parts without a continuation marker.");
            }

            marker = nextMarker.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return parts.OrderBy(part => part.PartNumber).ToArray();
    }

    private async Task<ImmutableStoredObject> RequireCompletedObjectAsync(
        MultipartHandle handle,
        CancellationToken cancellationToken)
    {
        var stored = await InspectObjectAsync(handle.ObjectKey, cancellationToken);
        if (stored is null)
        {
            throw new InvalidOperationException("Multipart upload is absent and the completed object does not exist.");
        }

        return stored;
    }

    private static void ValidateConsecutiveParts(IReadOnlyList<CompletedPart> parts)
    {
        for (var index = 0; index < parts.Count; index++)
        {
            if (parts[index].PartNumber != index + 1)
            {
                throw new InvalidOperationException("Multipart upload parts must be consecutive starting at 1.");
            }
        }
    }

    private static bool MatchesHandle(ImmutableStoredObject stored, MultipartHandle handle)
        => string.Equals(stored.ObjectKey, handle.ObjectKey, StringComparison.Ordinal) &&
           stored.ByteSize == handle.ExpectedByteSize &&
           string.Equals(stored.Sha256, handle.ExpectedSha256, StringComparison.OrdinalIgnoreCase);

    private static bool IsObjectNotFound(AmazonS3Exception exception)
        => exception.StatusCode == HttpStatusCode.NotFound ||
           string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.Ordinal) ||
           string.Equals(exception.ErrorCode, "NotFound", StringComparison.Ordinal);

    private static bool IsNoSuchUpload(AmazonS3Exception exception)
        => string.Equals(exception.ErrorCode, "NoSuchUpload", StringComparison.Ordinal) ||
           exception.StatusCode == HttpStatusCode.NotFound;

    private static string EncodeHandle(MultipartHandle handle)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(handle);
        return HandlePrefix + Convert.ToBase64String(json)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static MultipartHandle DecodeHandle(string providerUploadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerUploadId);
        if (!providerUploadId.StartsWith(HandlePrefix, StringComparison.Ordinal) ||
            providerUploadId.Length > MaximumEncodedHandleLength)
        {
            throw new ArgumentException("Invalid S3 multipart upload handle.", nameof(providerUploadId));
        }

        var encoded = providerUploadId[HandlePrefix.Length..]
            .Replace('-', '+')
            .Replace('_', '/');
        encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');

        byte[] json;
        try
        {
            json = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Invalid S3 multipart upload handle.", nameof(providerUploadId), exception);
        }

        MultipartHandle? handle;
        try
        {
            handle = JsonSerializer.Deserialize<MultipartHandle>(json);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Invalid S3 multipart upload handle.", nameof(providerUploadId), exception);
        }

        if (handle is null ||
            string.IsNullOrWhiteSpace(handle.ObjectKey) ||
            string.IsNullOrWhiteSpace(handle.NativeUploadId) ||
            handle.ExpectedByteSize <= 0 ||
            !IsValidSha256(handle.ExpectedSha256))
        {
            throw new ArgumentException("Invalid S3 multipart upload handle.", nameof(providerUploadId));
        }

        return handle with { ExpectedSha256 = NormalizeSha256(handle.ExpectedSha256) };
    }

    private static void ValidateExpectedObject(long expectedByteSize, string expectedSha256)
    {
        if (expectedByteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedByteSize));
        }

        if (!IsValidSha256(expectedSha256))
        {
            throw new ArgumentException("SHA-256 must be exactly 64 hexadecimal characters.", nameof(expectedSha256));
        }
    }

    private static void ValidateObjectKey(string objectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        if (Encoding.UTF8.GetByteCount(objectKey) > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(objectKey), "Object key is too long for S3-compatible storage.");
        }
    }

    private static void ValidateFutureExpiry(DateTimeOffset expiresAt)
    {
        if (expiresAt <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "Transfer authorization expiry must be in the future.");
        }
    }

    private static bool IsValidSha256(string value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Length == 64 &&
           value.All(Uri.IsHexDigit);

    private static string NormalizeSha256(string value) => value.ToUpperInvariant();

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record MultipartHandle(
        string ObjectKey,
        string NativeUploadId,
        long ExpectedByteSize,
        string ExpectedSha256);

    private sealed record CompletedPart(int PartNumber, long ByteSize, string ETag);
}
