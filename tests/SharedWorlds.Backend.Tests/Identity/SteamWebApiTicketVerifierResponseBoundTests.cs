using System.Net;
using SharedWorlds.Backend.Identity;
using Xunit;

namespace SharedWorlds.Backend.Tests.Identity;

public sealed class SteamWebApiTicketVerifierResponseBoundTests
{
    private const int OneMiB = 1024 * 1024;

    [Fact]
    public async Task DeclaredOversizeResponseIsRejectedBeforeJsonParsing()
    {
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[OneMiB + 1])
            })));
        var verifier = CreateVerifier(client);

        var exception = await Assert.ThrowsAsync<ExternalIdentityProviderException>(() =>
            verifier.VerifyAsync("A1B2"));

        Assert.Equal("steam", exception.Provider);
        Assert.Contains("safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publisher-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownLengthOversizeResponseIsRejectedWhileStreaming()
    {
        var content = new StreamContent(new GeneratedReadStream(OneMiB + 1));
        Assert.Null(content.Headers.ContentLength);
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            })));
        var verifier = CreateVerifier(client);

        var exception = await Assert.ThrowsAsync<ExternalIdentityProviderException>(() =>
            verifier.VerifyAsync("A1B2"));

        Assert.Equal("steam", exception.Provider);
        Assert.Contains("safety limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publisher-secret", exception.ToString(), StringComparison.Ordinal);
    }

    private static SteamWebApiTicketVerifier CreateVerifier(HttpClient client)
        => new(
            client,
            new SteamWebApiTicketVerifierOptions(
                appId: 480,
                publisherApiKey: "publisher-secret",
                identity: "steward-backend"));

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

    private sealed class GeneratedReadStream : Stream
    {
        private readonly long _length;
        private long _position;

        public GeneratedReadStream(long length)
        {
            _length = length;
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
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _length || buffer.IsEmpty)
            {
                return 0;
            }

            var read = (int)Math.Min(buffer.Length, _length - _position);
            buffer[..read].Fill((byte)'x');
            _position += read;
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
}
