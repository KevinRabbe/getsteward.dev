using System.Net;
using System.Security.Cryptography;
using System.Text;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class VerifiedPackageCacheAuthorizationValidationTests
{
    [Theory]
    [InlineData("../escape")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    [InlineData("ABCDEF")]
    public async Task CacheRejectsMalformedShaBeforeFilesystemOrNetworkUse(string sha256)
    {
        using var temp = new TemporaryDirectory();
        using var transferClient = new HttpClient(new DelegateHandler((_, _) =>
            throw new InvalidOperationException("Object storage must not be contacted.")));
        var cache = CreateCache(temp.Path, transferClient);

        var exception = await Assert.ThrowsAsync<PackageIntegrityException>(() =>
            cache.EnsureAsync(Authorization(sha256, byteSize: 4)));

        Assert.Contains("invalid SHA-256 digest", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Fact]
    public async Task CacheCanonicalizesShaIdentityAcrossLetterCase()
    {
        using var temp = new TemporaryDirectory();
        var bytes = Encoding.ASCII.GetBytes("test");
        var uppercaseSha = Convert.ToHexString(SHA256.HashData(bytes));
        var lowercaseSha = uppercaseSha.ToLowerInvariant();
        var calls = 0;
        using var transferClient = new HttpClient(new DelegateHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
        }));
        var cache = CreateCache(temp.Path, transferClient);

        var first = await cache.EnsureAsync(Authorization(lowercaseSha, bytes.LongLength));
        var second = await cache.EnsureAsync(Authorization(uppercaseSha, bytes.LongLength));

        Assert.Equal(1, calls);
        Assert.Equal(uppercaseSha, first.Sha256);
        Assert.Equal(first, second);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(first.Path));
    }

    [Fact]
    public async Task CacheRejectsInvalidByteSizeBeforeFilesystemOrNetworkUse()
    {
        using var temp = new TemporaryDirectory();
        using var transferClient = new HttpClient(new DelegateHandler((_, _) =>
            throw new InvalidOperationException("Object storage must not be contacted.")));
        var cache = CreateCache(temp.Path, transferClient);
        var sha = new string('A', 64);

        var exception = await Assert.ThrowsAsync<PackageIntegrityException>(() =>
            cache.EnsureAsync(Authorization(sha, byteSize: 0)));

        Assert.Contains("invalid byte size", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    private static VerifiedPackageCache CreateCache(string root, HttpClient client)
        => new(
            root,
            client,
            new VerifiedPackageCacheOptions(
                minimumFreeSpaceReserveBytes: 0,
                copyBufferBytes: 64 * 1024));

    private static AuthorizedPackageDownload Authorization(string sha256, long byteSize)
        => new(
            new Uri("https://objects.example/package"),
            new Dictionary<string, string>(),
            DateTimeOffset.UtcNow.AddMinutes(10),
            byteSize,
            sha256);

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
