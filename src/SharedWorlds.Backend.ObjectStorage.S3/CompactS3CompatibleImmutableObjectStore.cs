using System.IO.Compression;
using System.Text;
using SharedWorlds.Backend.Transfers;

namespace SharedWorlds.Backend.ObjectStorage.S3;

/// <summary>
/// Keeps restart-safe S3 multipart handles inside the durable transfer-store boundary without losing
/// any provider state. New handles are compressed deterministically; existing uncompressed handles
/// remain readable so in-flight transfers survive deployment of this format.
/// </summary>
public sealed class CompactS3CompatibleImmutableObjectStore : IPrivateImmutableObjectStore, IDisposable
{
    private const string CompactPrefix = "s3z1_";
    private const int MaximumDurableHandleLength = 512;
    private const int MaximumExpandedHandleBytes = 131_072;

    private readonly IPrivateImmutableObjectStore _inner;
    private readonly bool _ownsInner;
    private bool _disposed;

    public CompactS3CompatibleImmutableObjectStore(
        IPrivateImmutableObjectStore inner,
        bool ownsInner = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _ownsInner = ownsInner;
    }

    public async Task<ImmutableUploadSession> BeginMultipartUploadAsync(
        string objectKey,
        long expectedByteSize,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var session = await _inner.BeginMultipartUploadAsync(
            objectKey,
            expectedByteSize,
            expectedSha256,
            cancellationToken);
        return session with
        {
            ProviderUploadId = Compact(session.ProviderUploadId)
        };
    }

    public Task<ImmutableUploadSnapshot?> GetMultipartUploadAsync(
        string providerUploadId,
        CancellationToken cancellationToken = default)
        => _inner.GetMultipartUploadAsync(
            Expand(providerUploadId),
            cancellationToken);

    public Task<DirectObjectTransferAuthorization> AuthorizeUploadPartAsync(
        string providerUploadId,
        int partNumber,
        long expectedByteSize,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
        => _inner.AuthorizeUploadPartAsync(
            Expand(providerUploadId),
            partNumber,
            expectedByteSize,
            expiresAt,
            cancellationToken);

    public Task<ImmutableStoredObject> CompleteMultipartUploadAsync(
        string providerUploadId,
        CancellationToken cancellationToken = default)
        => _inner.CompleteMultipartUploadAsync(
            Expand(providerUploadId),
            cancellationToken);

    public Task AbortMultipartUploadAsync(
        string providerUploadId,
        CancellationToken cancellationToken = default)
        => _inner.AbortMultipartUploadAsync(
            Expand(providerUploadId),
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
        if (_ownsInner && _inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private static string Compact(string providerUploadId)
    {
        ValidateInput(providerUploadId);
        if (providerUploadId.StartsWith(CompactPrefix, StringComparison.Ordinal))
        {
            if (providerUploadId.Length > MaximumDurableHandleLength)
            {
                throw new InvalidOperationException(
                    "Compact S3 multipart handle exceeds the durable transfer-store boundary.");
            }

            _ = Expand(providerUploadId);
            return providerUploadId;
        }

        var source = Encoding.UTF8.GetBytes(providerUploadId);
        if (source.Length > MaximumExpandedHandleBytes)
        {
            throw new InvalidOperationException(
                "S3 multipart handle exceeds the bounded expanded-handle limit.");
        }

        using var output = new MemoryStream();
        using (var compressor = new BrotliStream(
                   output,
                   CompressionLevel.SmallestSize,
                   leaveOpen: true))
        {
            compressor.Write(source);
        }

        var compact = CompactPrefix + ToBase64Url(output.ToArray());
        if (compact.Length > MaximumDurableHandleLength)
        {
            throw new InvalidOperationException(
                "S3 multipart handle cannot be represented inside the durable transfer-store boundary.");
        }

        return compact;
    }

    private static string Expand(string providerUploadId)
    {
        ValidateInput(providerUploadId);
        if (!providerUploadId.StartsWith(CompactPrefix, StringComparison.Ordinal))
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
            compressed = FromBase64Url(providerUploadId[CompactPrefix.Length..]);
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
            var read = decompressor.Read(buffer, 0, buffer.Length);
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

        ValidateInput(expanded);
        return expanded;
    }

    private static void ValidateInput(string providerUploadId)
    {
        if (string.IsNullOrWhiteSpace(providerUploadId) ||
            providerUploadId.Any(char.IsControl))
        {
            throw new InvalidDataException(
                "S3 multipart handle must be non-empty and contain no control characters.");
        }
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

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
