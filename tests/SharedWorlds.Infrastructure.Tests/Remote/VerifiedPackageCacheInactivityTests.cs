using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class VerifiedPackageCacheInactivityTests
{
    private static readonly TimeSpan TestInactivityTimeout = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task MissingResponseHeadersFailsAfterInactivityWithoutCreatingCacheBytes()
    {
        using var temp = new TemporaryDirectory();
        using var client = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after cancellation.");
        }));
        var cache = CreateCache(temp.Path, client, TestInactivityTimeout);
        var expected = CreatePayload(100_000);
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<RemoteTransferInactivityTimeoutException>(() =>
            cache.EnsureAsync(Authorization(expected)));

        stopwatch.Stop();
        Assert.Equal(TestInactivityTimeout, exception.InactivityTimeout);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.partial", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.package", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task BodyProgressThenStallFailsAfterInactivityAndPreservesResumablePrefix()
    {
        using var temp = new TemporaryDirectory();
        const int prefixBytes = 40_000;
        var expected = CreatePayload(100_000);
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ProgressThenStallStream(expected, prefixBytes))
            })));
        var cache = CreateCache(temp.Path, client, TestInactivityTimeout);

        var exception = await Assert.ThrowsAsync<RemoteTransferInactivityTimeoutException>(() =>
            cache.EnsureAsync(Authorization(expected)));

        Assert.Equal(TestInactivityTimeout, exception.InactivityTimeout);
        var partial = Assert.Single(Directory.GetFiles(temp.Path, "*.partial", SearchOption.AllDirectories));
        Assert.Equal(prefixBytes, new FileInfo(partial).Length);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.package", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CallerCancellationIsNotRelabeledAsTransferInactivity()
    {
        using var temp = new TemporaryDirectory();
        using var client = new HttpClient(new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after cancellation.");
        }));
        var cache = CreateCache(temp.Path, client, TimeSpan.FromSeconds(10));
        var expected = CreatePayload(100_000);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var exception = await Record.ExceptionAsync(() =>
            cache.EnsureAsync(Authorization(expected), cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.IsNotType<RemoteTransferInactivityTimeoutException>(exception);
    }

    private static VerifiedPackageCache CreateCache(
        string root,
        HttpClient client,
        TimeSpan inactivityTimeout)
        => new(
            root,
            client,
            new VerifiedPackageCacheOptions(
                minimumFreeSpaceReserveBytes: 0,
                copyBufferBytes: 64 * 1024,
                maximumCacheBytes: 1024 * 1024,
                transferInactivityTimeout: inactivityTimeout));

    private static AuthorizedPackageDownload Authorization(byte[] expected)
        => new(
            new Uri("https://objects.example/package"),
            new Dictionary<string, string>(),
            DateTimeOffset.UtcNow.AddMinutes(10),
            expected.LongLength,
            Convert.ToHexString(SHA256.HashData(expected)));

    private static byte[] CreatePayload(int byteCount)
    {
        var bytes = new byte[byteCount];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)((index * 31 + 19) % 251);
        }

        return bytes;
    }

    private sealed class ProgressThenStallStream : Stream
    {
        private readonly byte[] _source;
        private readonly int _prefixBytes;
        private int _position;

        public ProgressThenStallStream(byte[] source, int prefixBytes)
        {
            _source = source;
            _prefixBytes = prefixBytes;
        }

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
            => throw new NotSupportedException("The test exercises the async transfer path.");

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position < _prefixBytes)
            {
                var read = Math.Min(buffer.Length, _prefixBytes - _position);
                _source.AsMemory(_position, read).CopyTo(buffer);
                _position += read;
                return read;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
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
