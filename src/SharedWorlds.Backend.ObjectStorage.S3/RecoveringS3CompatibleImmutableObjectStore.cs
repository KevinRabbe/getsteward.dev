using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using SharedWorlds.Backend.Transfers;

namespace SharedWorlds.Backend.ObjectStorage.S3;

/// <summary>
/// Restart-safe S3-compatible object-store facade. Begin recovers an in-progress multipart upload
/// for the exact immutable object key before creating another provider upload. New durable transfer
/// rows store only a bounded integrity-addressed handle reference; the exact opaque provider handle
/// remains in a backend-only S3 sidecar. Legacy unreferenced handles remain readable.
/// </summary>
public sealed class RecoveringS3CompatibleImmutableObjectStore : IPrivateImmutableObjectStore, IDisposable
{
    private const string HandlePrefix = "s3mp1_";
    private const string ReferencePrefix = "s3ref1_";
    private const string ReferenceObjectPrefix = "steward-system/private-upload-handles/";
    private const int MaximumDurableHandleLength = 512;
    private const int MaximumProviderHandleBytes = 131_072;
    private const int Sha256HexLength = 64;

    private readonly IAmazonS3 _client;
    private readonly string _bucketName;
    private readonly S3CompatibleImmutableObjectStore _inner;
    private readonly bool _ownsClient;
    private bool _disposed;

    public RecoveringS3CompatibleImmutableObjectStore(
        IAmazonS3 client,
        string bucketName,
        Protocol presignedProtocol,
        bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        _client = client;
        _bucketName = bucketName;
        _inner = new S3CompatibleImmutableObjectStore(
            client,
            bucketName,
            presignedProtocol,
            ownsClient: false);
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

        var existing = await FindExistingUploadAsync(objectKey, cancellationToken);
        string providerHandle;
        if (existing is not null)
        {
            providerHandle = EncodeHandle(new MultipartHandle(
                objectKey,
                existing.UploadId,
                expectedByteSize,
                NormalizeSha256(expectedSha256)));
        }
        else
        {
            var created = await _inner.BeginMultipartUploadAsync(
                objectKey,
                expectedByteSize,
                expectedSha256,
                cancellationToken);
            if (!string.Equals(created.ObjectKey, objectKey, StringComparison.Ordinal))
            {
                await _inner.AbortMultipartUploadAsync(
                    created.ProviderUploadId,
                    cancellationToken);
                throw new InvalidOperationException(
                    "S3-compatible storage created a multipart upload for the wrong object key.");
            }

            providerHandle = created.ProviderUploadId;
        }

        return new ImmutableUploadSession(
            await PersistHandleReferenceAsync(providerHandle, cancellationToken),
            objectKey);
    }

    public async Task<ImmutableUploadSnapshot?> GetMultipartUploadAsync(
        string providerUploadId,
        CancellationToken cancellationToken = default)
        => await _inner.GetMultipartUploadAsync(
            await ResolveProviderHandleAsync(providerUploadId, cancellationToken),
            cancellationToken);

    public async Task<DirectObjectTransferAuthorization> AuthorizeUploadPartAsync(
        string providerUploadId,
        int partNumber,
        long expectedByteSize,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
        => await _inner.AuthorizeUploadPartAsync(
            await ResolveProviderHandleAsync(providerUploadId, cancellationToken),
            partNumber,
            expectedByteSize,
            expiresAt,
            cancellationToken);

    public async Task<ImmutableStoredObject> CompleteMultipartUploadAsync(
        string providerUploadId,
        CancellationToken cancellationToken = default)
        => await _inner.CompleteMultipartUploadAsync(
            await ResolveProviderHandleAsync(providerUploadId, cancellationToken),
            cancellationToken);

    public async Task AbortMultipartUploadAsync(
        string providerUploadId,
        CancellationToken cancellationToken = default)
    {
        var handle = await ResolveProviderHandleAsync(providerUploadId, cancellationToken);
        await _inner.AbortMultipartUploadAsync(handle, cancellationToken);
        await DeleteExactHandleReferenceAsync(providerUploadId, cancellationToken);
    }

    public Task<ImmutableStoredObject?> InspectObjectAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
        => _inner.InspectObjectAsync(objectKey, cancellationToken);

    public Task<DirectObjectTransferAuthorization> AuthorizeDownloadAsync(
        string objectKey,
        long expectedByteSize,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
        => _inner.AuthorizeDownloadAsync(
            objectKey,
            expectedByteSize,
            expiresAt,
            cancellationToken);

    public async Task DeleteObjectAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        await _inner.DeleteObjectAsync(objectKey, cancellationToken);
        await DeleteHandleReferencesForObjectAsync(objectKey, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _inner.Dispose();
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private async Task<MultipartUpload?> FindExistingUploadAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        string? keyMarker = null;
        string? uploadIdMarker = null;
        MultipartUpload? selected = null;

        while (true)
        {
            var response = await _client.ListMultipartUploadsAsync(
                new ListMultipartUploadsRequest
                {
                    BucketName = _bucketName,
                    Prefix = objectKey,
                    MaxUploads = 1000,
                    KeyMarker = keyMarker,
                    UploadIdMarker = uploadIdMarker
                },
                cancellationToken);

            foreach (var upload in response.MultipartUploads ?? [])
            {
                if (!string.Equals(upload.Key, objectKey, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(upload.UploadId))
                {
                    continue;
                }

                if (selected is null || CompareUploads(upload, selected) < 0)
                {
                    selected = upload;
                }
            }

            if (response.IsTruncated != true)
            {
                return selected;
            }

            if (string.IsNullOrWhiteSpace(response.NextKeyMarker))
            {
                throw new InvalidOperationException(
                    "S3-compatible storage returned a truncated multipart-upload list without a continuation key.");
            }

            keyMarker = response.NextKeyMarker;
            uploadIdMarker = response.NextUploadIdMarker;
        }
    }

    private async Task<string> PersistHandleReferenceAsync(
        string providerHandle,
        CancellationToken cancellationToken)
    {
        ValidateProviderHandle(providerHandle);
        if (providerHandle.StartsWith(ReferencePrefix, StringComparison.Ordinal))
        {
            _ = await ResolveProviderHandleAsync(providerHandle, cancellationToken);
            return providerHandle;
        }

        var decoded = DecodeLegacyHandle(providerHandle);
        var bytes = Encoding.UTF8.GetBytes(providerHandle);
        var objectKeyHash = ComputeSha256Hex(Encoding.UTF8.GetBytes(decoded.ObjectKey));
        var handleHash = ComputeSha256Hex(bytes);
        var reference = $"{ReferencePrefix}{objectKeyHash}_{handleHash}";
        if (reference.Length > MaximumDurableHandleLength)
        {
            throw new InvalidOperationException(
                "S3 multipart handle reference exceeds the durable transfer-store boundary.");
        }

        var key = GetReferenceObjectKey(objectKeyHash, handleHash);
        var existing = await TryReadReferenceObjectAsync(key, cancellationToken);
        if (existing is not null)
        {
            RequireExactReferenceBytes(handleHash, existing);
            return reference;
        }

        await using var input = new MemoryStream(bytes, writable: false);
        await _client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = key,
                InputStream = input,
                AutoCloseStream = false,
                ContentType = "application/octet-stream"
            },
            cancellationToken);

        var persisted = await TryReadReferenceObjectAsync(key, cancellationToken)
            ?? throw new InvalidOperationException(
                "S3-compatible storage did not persist the multipart handle reference.");
        RequireExactReferenceBytes(handleHash, persisted);
        return reference;
    }

    private async Task<string> ResolveProviderHandleAsync(
        string providerUploadId,
        CancellationToken cancellationToken)
    {
        ValidateProviderHandle(providerUploadId);
        if (!providerUploadId.StartsWith(ReferencePrefix, StringComparison.Ordinal))
        {
            return providerUploadId;
        }

        var reference = ParseReference(providerUploadId);
        var bytes = await TryReadReferenceObjectAsync(
            GetReferenceObjectKey(reference.ObjectKeyHash, reference.HandleHash),
            cancellationToken)
            ?? throw new InvalidDataException(
                "S3 multipart handle reference object is missing.");
        RequireExactReferenceBytes(reference.HandleHash, bytes);

        string providerHandle;
        try
        {
            providerHandle = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "S3 multipart handle reference does not contain valid UTF-8.",
                exception);
        }

        var decoded = DecodeLegacyHandle(providerHandle);
        var actualObjectKeyHash = ComputeSha256Hex(
            Encoding.UTF8.GetBytes(decoded.ObjectKey));
        if (!string.Equals(
                actualObjectKeyHash,
                reference.ObjectKeyHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "S3 multipart handle reference belongs to a different object key.");
        }

        return providerHandle;
    }

    private async Task DeleteExactHandleReferenceAsync(
        string providerUploadId,
        CancellationToken cancellationToken)
    {
        if (!providerUploadId.StartsWith(ReferencePrefix, StringComparison.Ordinal))
        {
            return;
        }

        var reference = ParseReference(providerUploadId);
        await _client.DeleteObjectAsync(
            _bucketName,
            GetReferenceObjectKey(reference.ObjectKeyHash, reference.HandleHash),
            cancellationToken);
    }

    private async Task DeleteHandleReferencesForObjectAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        ValidateObjectKey(objectKey);
        var objectKeyHash = ComputeSha256Hex(Encoding.UTF8.GetBytes(objectKey));
        var prefix = $"{ReferenceObjectPrefix}{objectKeyHash}/";
        string? continuationToken = null;

        do
        {
            var response = await _client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = _bucketName,
                    Prefix = prefix,
                    MaxKeys = 1000,
                    ContinuationToken = continuationToken
                },
                cancellationToken);

            foreach (var entry in response.S3Objects ?? [])
            {
                if (!string.IsNullOrWhiteSpace(entry.Key))
                {
                    await _client.DeleteObjectAsync(
                        _bucketName,
                        entry.Key,
                        cancellationToken);
                }
            }

            continuationToken = response.IsTruncated == true
                ? response.NextContinuationToken
                : null;
            if (response.IsTruncated == true && string.IsNullOrWhiteSpace(continuationToken))
            {
                throw new InvalidOperationException(
                    "S3-compatible storage returned a truncated handle-reference list without a continuation token.");
            }
        }
        while (continuationToken is not null);
    }

    private async Task<byte[]?> TryReadReferenceObjectAsync(
        string key,
        CancellationToken cancellationToken)
    {
        GetObjectResponse response;
        try
        {
            response = await _client.GetObjectAsync(
                _bucketName,
                key,
                cancellationToken);
        }
        catch (AmazonS3Exception exception) when (IsObjectNotFound(exception))
        {
            return null;
        }

        using (response)
        await using (var output = new MemoryStream())
        {
            var buffer = new byte[4096];
            while (true)
            {
                var read = await response.ResponseStream.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > MaximumProviderHandleBytes)
                {
                    throw new InvalidDataException(
                        "S3 multipart handle reference exceeds its bounded byte size.");
                }

                await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
            }

            return output.ToArray();
        }
    }

    private static HandleReference ParseReference(string value)
    {
        var expectedLength = ReferencePrefix.Length + Sha256HexLength + 1 + Sha256HexLength;
        if (value.Length != expectedLength ||
            value[ReferencePrefix.Length + Sha256HexLength] != '_')
        {
            throw new InvalidDataException(
                "S3 multipart handle reference has the wrong shape.");
        }

        var objectKeyHash = value.Substring(ReferencePrefix.Length, Sha256HexLength);
        var handleHash = value[(ReferencePrefix.Length + Sha256HexLength + 1)..];
        if (!IsSha256Hex(objectKeyHash) || !IsSha256Hex(handleHash))
        {
            throw new InvalidDataException(
                "S3 multipart handle reference contains malformed SHA-256 evidence.");
        }

        return new HandleReference(
            objectKeyHash.ToLowerInvariant(),
            handleHash.ToLowerInvariant());
    }

    private static MultipartHandle DecodeLegacyHandle(string providerHandle)
    {
        ValidateProviderHandle(providerHandle);
        if (!providerHandle.StartsWith(HandlePrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "S3 multipart handle reference does not contain a supported provider handle.");
        }

        byte[] json;
        try
        {
            json = FromBase64Url(providerHandle[HandlePrefix.Length..]);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "S3 multipart handle is malformed.",
                exception);
        }

        MultipartHandle handle;
        try
        {
            handle = JsonSerializer.Deserialize<MultipartHandle>(json)
                ?? throw new InvalidDataException("S3 multipart handle payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "S3 multipart handle payload is malformed.",
                exception);
        }

        ValidateObjectKey(handle.ObjectKey);
        if (string.IsNullOrWhiteSpace(handle.NativeUploadId) ||
            handle.NativeUploadId.Any(char.IsControl))
        {
            throw new InvalidDataException(
                "S3 multipart handle contains an invalid native upload ID.");
        }

        ValidateExpectedObject(handle.ExpectedByteSize, handle.ExpectedSha256);
        return handle;
    }

    private static void RequireExactReferenceBytes(string expectedHash, byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumProviderHandleBytes)
        {
            throw new InvalidDataException(
                "S3 multipart handle reference has an invalid byte size.");
        }

        var actualHash = ComputeSha256Hex(bytes);
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "S3 multipart handle reference failed its SHA-256 integrity check.");
        }
    }

    private static bool IsObjectNotFound(AmazonS3Exception exception)
        => exception.StatusCode == HttpStatusCode.NotFound ||
           string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.Ordinal) ||
           string.Equals(exception.ErrorCode, "NoSuchObject", StringComparison.Ordinal);

    private static string GetReferenceObjectKey(
        string objectKeyHash,
        string handleHash)
        => $"{ReferenceObjectPrefix}{objectKeyHash}/{handleHash}.handle";

    private static int CompareUploads(MultipartUpload left, MultipartUpload right)
    {
        var initiated = Nullable.Compare(left.Initiated, right.Initiated);
        return initiated != 0
            ? initiated
            : string.Compare(left.UploadId, right.UploadId, StringComparison.Ordinal);
    }

    private static string EncodeHandle(MultipartHandle handle)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(handle);
        return HandlePrefix + ToBase64Url(json);
    }

    private static string ToBase64Url(byte[] value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value
            .Replace('-', '+')
            .Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid Base64Url length.")
        };
        return Convert.FromBase64String(padded);
    }

    private static string ComputeSha256Hex(ReadOnlySpan<byte> value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static bool IsSha256Hex(string value)
        => value.Length == Sha256HexLength && value.All(Uri.IsHexDigit);

    private static void ValidateExpectedObject(long expectedByteSize, string expectedSha256)
    {
        if (expectedByteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedByteSize));
        }

        if (!IsSha256Hex(expectedSha256))
        {
            throw new ArgumentException(
                "SHA-256 must be exactly 64 hexadecimal characters.",
                nameof(expectedSha256));
        }
    }

    private static void ValidateObjectKey(string objectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        if (Encoding.UTF8.GetByteCount(objectKey) > 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(objectKey),
                "Object key is too long for S3-compatible storage.");
        }
    }

    private static void ValidateProviderHandle(string providerUploadId)
    {
        if (string.IsNullOrWhiteSpace(providerUploadId) ||
            Encoding.UTF8.GetByteCount(providerUploadId) > MaximumProviderHandleBytes ||
            providerUploadId.Any(char.IsControl))
        {
            throw new InvalidDataException(
                "S3 multipart handle is empty, malformed, or exceeds its bounded byte size.");
        }
    }

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

    private sealed record HandleReference(
        string ObjectKeyHash,
        string HandleHash);
}
