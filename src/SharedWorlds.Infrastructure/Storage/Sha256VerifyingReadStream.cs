using System.Security.Cryptography;

namespace SharedWorlds.Infrastructure.Storage;

internal sealed class Sha256VerifyingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly byte[] _expectedHash;
    private readonly string _description;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private InvalidDataException? _integrityFailure;
    private bool _verified;
    private bool _disposed;

    public Sha256VerifyingReadStream(
        Stream inner,
        byte[] expectedHash,
        string description)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(expectedHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (!inner.CanRead)
        {
            throw new ArgumentException("The wrapped stream must be readable.", nameof(inner));
        }

        if (expectedHash.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException("Expected SHA-256 digest must contain exactly 32 bytes.", nameof(expectedHash));
        }

        _inner = inner;
        _expectedHash = expectedHash.ToArray();
        _description = description;
    }

    public override bool CanRead => !_disposed && _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
        => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        Observe(buffer.AsSpan(offset, read));
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _inner.Read(buffer);
        Observe(buffer[..read]);
        return read;
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        var read = await _inner.ReadAsync(
            buffer.AsMemory(offset, count),
            cancellationToken);
        Observe(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken);
        Observe(buffer.Span[..read]);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _hash.Dispose();
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _hash.Dispose();
            await _inner.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private void Observe(ReadOnlySpan<byte> bytes)
    {
        if (_integrityFailure is not null)
        {
            throw _integrityFailure;
        }

        if (bytes.Length > 0)
        {
            if (_verified)
            {
                throw new InvalidOperationException(
                    "Cannot read additional bytes after SHA-256 verification completed.");
            }

            _hash.AppendData(bytes);
            return;
        }

        VerifyAtEndOfStream();
    }

    private void VerifyAtEndOfStream()
    {
        if (_verified)
        {
            return;
        }

        var actualHash = _hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actualHash, _expectedHash))
        {
            _integrityFailure = new InvalidDataException(
                $"{_description} failed SHA-256 integrity verification.");
            throw _integrityFailure;
        }

        _verified = true;
    }
}
