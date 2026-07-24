using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class VerifiedPackageCacheStreamBoundTests
{
    private const int ExpectedBytes = 100_000;

    [Fact]
    public async Task FreshOversizeResponseReadsOnlyAuthorizedBytesPlusDetectionByte()
    {
        using var temp = new TemporaryDirectory();
        var source = new CountingPatternStream(totalBytes: 4 * 1024 * 1024);
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            Assert.Null(request.Headers.Range);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(source)
            });
        }));
        var cache = CreateCache(temp.Path, client);

        var exception = await Assert.ThrowsAsync<PackageIntegrityException>(() =>
            cache.EnsureAsync(Authorization(ExpectedBytes)));

        Assert.Contains("exceeded", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ExpectedBytes + 1L, source.BytesRead);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.partial", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.package", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ResumedResponseWithValidRangeButExtraBodyNeverWritesPastAuthorizedTotal()
    {
        using var temp = new TemporaryDirectory();
        const int prefixBytes = 40_000;
        var calls = 0;
        CountingPatternStream? resumedSource = null;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Null(request.Headers.Range);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new CountingPatternStream(prefixBytes))
                });
            }

            Assert.Equal(prefixBytes, request.Headers.Range?.Ranges.Single().From);
            resumedSource = new CountingPatternStream(totalBytes: 2 * 1024 * 1024, absoluteOffset: prefixBytes);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(resumedSource)
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                prefixBytes,
                ExpectedBytes - 1,
                ExpectedBytes);
            return Task.FromResult(response);
        }));
        var cache = CreateCache(temp.Path, client);

        await Assert.ThrowsAsync<IncompletePackageDownloadException>(() =>
            cache.EnsureAsync(Authorization(ExpectedBytes)));
        var partial = Assert.Single(Directory.GetFiles(temp.Path, "*.partial", SearchOption.AllDirectories));
        Assert.Equal(prefixBytes, new FileInfo(partial).Length);

        var exception = await Assert.ThrowsAsync<PackageIntegrityException>(() =>
            cache.EnsureAsync(Authorization(ExpectedBytes)));

        Assert.Contains("exceeded", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(resumedSource);
        Assert.Equal(ExpectedBytes - prefixBytes + 1L, resumedSource.BytesRead);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.partial", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.package", SearchOption.AllDirectories));
    }

    private static VerifiedPackageCache CreateCache(string root, HttpClient client)
        => new(
            root,
            client,
            new VerifiedPackageCacheOptions(
                minimumFreeSpaceReserveBytes: 0,
                copyBufferBytes: 64 * 1024));

    private static AuthorizedPackageDownload Authorization(long byteSize)
    {
        var expectedBytes = CreatePatternBytes(checked((int)byteSize));
        return new AuthorizedPackageDownload(
            new Uri("https://objects.example/package"),
            new Dictionary<string, string>(),
            DateTimeOffset.UtcNow.AddMinutes(10),
            byteSize,
            Convert.ToHexString(SHA256.HashData(expectedBytes)));
    }

    private static byte[] CreatePatternBytes(int byteCount)
    {
        var bytes = new byte[byteCount];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = PatternByte(index);
        }

        return bytes;
    }

    private static byte PatternByte(long absoluteOffset)
        => (byte)((absoluteOffset * 29 + 11) % 251);

    private sealed class CountingPatternStream : Stream
    {
        private readonly long _totalBytes;
        private readonly long _absoluteOffset;
        private long _position;

        public CountingPatternStream(long totalBytes, long absoluteOffset = 0)
        {
            _totalBytes = totalBytes;
            _absoluteOffset = absoluteOffset;
        }

        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _totalBytes || buffer.IsEmpty)
            {
                return 0;
            }

            var read = (int)Math.Min(buffer.Length, _totalBytes - _position);
            for (var index = 0; index < read; index++)
            {
                buffer[index] = PatternByte(_absoluteOffset + _position + index);
            }

            _position += read;
            BytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "SharedWorlds.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
