using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using SharedWorlds.Backend.Transfers;

namespace SharedWorlds.Backend.ObjectStorage.S3;

/// <summary>
/// Restart-safe S3-compatible object-store facade. Begin recovers an in-progress multipart upload
/// for the exact immutable object key before creating another provider upload. Newly returned provider
/// handles are compressed inside the durable 512-character transfer-store boundary; legacy handles
/// remain readable so in-flight transfers survive deployment of the compact format.
/// </summary>
public sealed class RecoveringS3CompatibleImmutableObjectStore : IPrivateImmutableObjectStore, IDisposable
{
    private const string HandlePrefix = "s3mp1_";
    private const string CompactHandlePrefix = "s3z1_";
    private const int MaximumDurableHandleLength = 512;
    private const int MaximumExpandedHandleBytes = 131_072;

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
        if (existing is not null)
        {
            var legacyHandle = EncodeHandle(new MultipartHandle(
                objectKey,
                existing.UploadId,
                expectedByteSize,
                NormalizeSha256(expectedSha256)));
            return new ImmutableUploadSession(
                CompactHandle(legacyHandle),
                objectKey);
        }

        var created = await _inner.BeginMultipartUploadAsync(
            objectKey,
            expectedByteSize,
            expectedSha256,
            cancellationToken);
        return created with
        {
            ProviderUploadId = CompactHandle(created.ProviderUploadId)
        };
    }

    public Task<ImmutableUploadSnapshot?> GetMultipartUploadAsync(
        string providerUploadId,
        CancellationToken cancellationToken = default)
        => _inner.GetMultipartUploadAsync(
            ExpandHandle(providerUploadId),
            cancellationToken);

    public Task<DirectObjectTransferAuthorization> AuthorizeUploadPartAsync(
        string providerUploadId,
        int partNumber,
        long expectedByteSize,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
        => _inner.AuthorizeUploadPartAsync(
            ExpandHandle(providerUploadId),
            partNumber,
            expectedByteSize,
            expiresAt,
            cancellationToken);

    public Task<ImmutableStoredObject> CompleteMultipartUploadAsync(
        string providerUploadId,
        CancellationToken cancellationToken = default)
        => _inner.CompleteMultipartUploadAsync(
            ExpandHandle(providerUploadId),
            cancellationToken);

    public Task AbortMultipartUploadAsync(
        string providerUploadId,
        CancellationToken cancellationToken = default)
        => _inner.AbortMultipartUploadAsync(
            ExpandHandle(providerUploadId),
            cancellationToken);

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

    private static int CompareUploads(MultipartUpload left, MultipartUpload right)
    {
        var initiated = Nullable.Compare(left.Initiated, right.Initiated);
        return initiated != 0
            ? initiated
            : string.Compare(left.UploadId, right.UploadId, StringComparison.Ordinal);
    }

    private static string CompactHandle(string providerUploadId)
    {
        ValidateProviderHandle(providerUploadId, MaximumExpandedHandleBytes);
        if (providerUploadId.StartsWith(CompactHandlePrefix, StringComparison.Ordinal))
        {
            if (providerUploadId.Length > MaximumDurableHandleLength)
            {
                throw new InvalidOperationException(
                    "Compact S3 multipart handle exceeds the durable transfer-store boundary.");
            }

            _ = ExpandHandle(providerUploadId);
            return providerUploadId;
        }

        var source = Encoding.UTF8.GetBytes(providerUploadId);
        using var output = new MemoryStream();
        using (var compressor = new BrotliStream(
                   output,
                   CompressionLevel.SmallestSize,
                   leaveOpen: true))
        {
            compressor.Write(source);
        }

        var compact = CompactHandlePrefix + ToBase64Url(output.ToArray());
        if (compact.Length > MaximumDurableHandleLength)
        {
            throw new InvalidOperationException(
                "S3 multipart handle cannot be represented inside the durable transfer-store boundary.");
        }

        return compact;
    }

    private static string ExpandHandle(string providerUploadId)
    {
        ValidateProviderHandle(providerUploadId, MaximumExpandedHandleBytes);
        if (!providerUploadId.StartsWith(CompactHandlePrefix, StringComparison.Ordinal))
        {
            return providerUploadId;
        }

        if (providerUploadId.Length > MaximumDurableHandleLength)
        {
            throw new InvalidDataException(
                "Compact S3 multipart handle exceeds the durable transfer-store boundary.");
        }

        byte[] compressed;
        try
        {
            compressed = FromBase64Url(providerUploadId[CompactHandlePrefix.Length..]);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException(
                "Compact S3 multipart handle is malformed.",
                exception);
        }

        using var input = new MemoryStream(compressed, writable: false);
        using var decompressor = new BrotliStream(
            input,
            CompressionMode.Decompress,
            leaveOpen: false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            int read;
            try
            {
                read = decompressor.Read(buffer, 0, buffer.Length);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(
                    "Compact S3 multipart handle contains invalid compressed data.",
                    exception);
            }

            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaximumExpandedHandleBytes)
            {
                throw new InvalidDataException(
                    "Compact S3 multipart handle expands beyond its bounded limit.");
            }

            output.Write(buffer, 0, read);
        }

        string expanded;
        try
        {
            expanded = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true)
                .GetString(output.ToArray());
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "Compact S3 multipart handle does not contain valid UTF-8.",
                exception);
        }

        ValidateProviderHandle(expanded, MaximumExpandedHandleBytes);
        return expanded;
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

    private static void ValidateProviderHandle(string providerUploadId, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(providerUploadId) ||
            providerUploadId.Length > maximumLength ||
            providerUploadId.Any(char.IsControl))
        {
            throw new InvalidDataException(
                "S3 multipart handle is empty, malformed, or exceeds its bounded length.");
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
