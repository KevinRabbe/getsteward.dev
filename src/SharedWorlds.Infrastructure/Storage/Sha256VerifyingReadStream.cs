using System.Security.Cryptography;

namespace SharedWorlds.Infrastructure.Storage;

internal sealed class Sha256VerifyingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly byte[] _expectedHash;
    private readonly string _description;
    private readonly long _expectedLength;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _observedBytes;
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
        if (!inner.CanRead || !inner.CanSeek)
        {
            throw new ArgumentException(
                "The wrapped stream must be readable and seekable for integrity verification.",
                nameof(inner));
        }

        if (inner.Position != 0)
        {
            throw new ArgumentException(
                "The wrapped stream must be positioned at byte 0 for integrity verification.",
                nameof(inner));
        }

        if (expectedHash.Length != SHA256.HashSizeInBytes)
        {
            throw new ArgumentException("Expected SHA-256 digest must contain exactly 32 bytes.", nameof(expectedHash));
        }

        _inner = inner;
        _expectedHash = expectedHash.ToArray();
        _description = description;
        _expectedLength = inner.Length;
    }

    public override bool CanRead => !_disposed && _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _expectedLength;

    public override long Position
    {
        get => _observedBytes;
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

        if (_verified)
        {
            if (bytes.Length == 0)
            {
                return;
            }

            throw new InvalidOperationException(
                "Cannot read additional bytes after SHA-256 verification completed.");
        }

        if (bytes.Length == 0)
        {
            if (_observedBytes != _expectedLength)
            {
                FailIntegrity(
                    $"{_description} ended after {_observedBytes} bytes, expected {_expectedLength} bytes.");
            }

            Verify();
            return;
        }

        try
        {
            _observedBytes = checked(_observedBytes + bytes.Length);
        }
        catch (OverflowException)
        {
            FailIntegrity($"{_description} exceeded its expected byte length.");
        }

        if (_observedBytes > _expectedLength)
        {
            FailIntegrity(
                $"{_description} exceeded its expected length of {_expectedLength} bytes.");
        }

        _hash.AppendData(bytes);
        if (_observedBytes == _expectedLength)
        {
            Verify();
        }
    }

    private void Verify()
    {
        if (_verified)
        {
            return;
        }

        var actualHash = _hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actualHash, _expectedHash))
        {
            FailIntegrity($"{_description} failed SHA-256 integrity verification.");
        }

        _verified = true;
    }

    private void FailIntegrity(string message)
    {
        _integrityFailure = new InvalidDataException(message);
        throw _integrityFailure;
    }
}
