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
    {
        var handle = await ResolveProviderHandleAsync(providerUploadId, cancellationToken);
        var stored = await _inner.CompleteMultipartUploadAsync(handle, cancellationToken);
        await DeleteHandleReferenceAsync(providerUploadId, cancellationToken);
        return stored;
    }

    public async Task AbortMultipartUploadAsync(
        string providerUploadId,
        CancellationToken cancellationToken = default)
    {
        var handle = await ResolveProviderHandleAsync(providerUploadId, cancellationToken);
        await _inner.AbortMultipartUploadAsync(handle, cancellationToken);
        await DeleteHandleReferenceAsync(providerUploadId, cancellationToken);
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

    public Task DeleteObjectAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
        => _inner.DeleteObjectAsync(objectKey, cancellationToken);

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

        var bytes = Encoding.UTF8.GetBytes(providerHandle);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var reference = ReferencePrefix + hash;
        if (reference.Length > MaximumDurableHandleLength)
        {
            throw new InvalidOperationException(
                "S3 multipart handle reference exceeds the durable transfer-store boundary.");
        }

        var key = GetReferenceObjectKey(hash);
        var existing = await TryReadReferenceObjectAsync(key, cancellationToken);
        if (existing is not null)
        {
            RequireExactReferenceBytes(hash, existing);
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
        RequireExactReferenceBytes(hash, persisted);
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

        if (providerUploadId.Length != ReferencePrefix.Length + 64)
        {
            throw new InvalidDataException(
                "S3 multipart handle reference has the wrong length.");
        }

        var hash = providerUploadId[ReferencePrefix.Length..];
        if (hash.Any(value => !Uri.IsHexDigit(value)))
        {
            throw new InvalidDataException(
                "S3 multipart handle reference contains a malformed SHA-256.");
        }

        var bytes = await TryReadReferenceObjectAsync(
            GetReferenceObjectKey(hash.ToLowerInvariant()),
            cancellationToken)
            ?? throw new InvalidDataException(
                "S3 multipart handle reference object is missing.");
        RequireExactReferenceBytes(hash, bytes);

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

        ValidateProviderHandle(providerHandle);
        if (!providerHandle.StartsWith(HandlePrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "S3 multipart handle reference does not contain a supported provider handle.");
        }

        return providerHandle;
    }

    private async Task DeleteHandleReferenceAsync(
        string providerUploadId,
        CancellationToken cancellationToken)
    {
        if (!providerUploadId.StartsWith(ReferencePrefix, StringComparison.Ordinal))
        {
            return;
        }

        if (providerUploadId.Length != ReferencePrefix.Length + 64)
        {
            throw new InvalidDataException(
                "S3 multipart handle reference has the wrong length.");
        }

        await _client.DeleteObjectAsync(
            _bucketName,
            GetReferenceObjectKey(
                providerUploadId[ReferencePrefix.Length..].ToLowerInvariant()),
            cancellationToken);
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

    private static void RequireExactReferenceBytes(string expectedHash, byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumProviderHandleBytes)
        {
            throw new InvalidDataException(
                "S3 multipart handle reference has an invalid byte size.");
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
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

    private static string GetReferenceObjectKey(string hash)
        => $"{ReferenceObjectPrefix}{hash}.handle";

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
        return HandlePrefix + Convert.ToBase64String(json)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static void ValidateExpectedObject(long expectedByteSize, string expectedSha256)
    {
        if (expectedByteSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedByteSize));
        }

        if (string.IsNullOrWhiteSpace(expectedSha256) ||
            expectedSha256.Length != 64 ||
            expectedSha256.Any(value => !Uri.IsHexDigit(value)))
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
}
