using System.Net;
using System.Security.Cryptography;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class VerifiedPackageCacheRetentionTests
{
    [Fact]
    public async Task PackageLargerThanConfiguredCacheFailsBeforeNetwork()
    {
        using var temp = new TemporaryDirectory();
        var calls = 0;
        using var client = new HttpClient(new DelegateHandler((_, _) =>
        {
            calls++;
            throw new InvalidOperationException("Network must not be used when the package cannot fit the configured cache.");
        }));
        var bytes = CreatePayload(150_000, seed: 1);
        var cache = CreateCache(temp.Path, client, maximumCacheBytes: 100_000);

        var exception = await Assert.ThrowsAsync<PackageCacheCapacityException>(() =>
            cache.EnsureAsync(Authorization("too-large", bytes)));

        Assert.Equal(150_000, exception.RequiredBytes);
        Assert.Equal(100_000, exception.MaximumBytes);
        Assert.Equal(0, calls);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.partial", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.package", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CacheHitRefreshesRecencyAndOldestDisposablePackageIsEvicted()
    {
        using var temp = new TemporaryDirectory();
        var first = CreatePayload(150_000, seed: 2);
        var second = CreatePayload(150_000, seed: 3);
        var third = CreatePayload(150_000, seed: 4);
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["/first"] = first,
            ["/second"] = second,
            ["/third"] = third
        };
        var calls = 0;
        using var client = new HttpClient(new DelegateHandler((request, _) =>
        {
            calls++;
            var bytes = payloads[request.RequestUri!.AbsolutePath];
            return Task.FromResult(Bytes(bytes));
        }));
        var cache = CreateCache(temp.Path, client, maximumCacheBytes: 300_000);
        var firstAuthorization = Authorization("first", first);
        var secondAuthorization = Authorization("second", second);
        var thirdAuthorization = Authorization("third", third);

        var firstCached = await cache.EnsureAsync(firstAuthorization);
        var secondCached = await cache.EnsureAsync(secondAuthorization);
        File.SetLastWriteTimeUtc(firstCached.Path, DateTime.UtcNow.AddHours(-3));
        File.SetLastWriteTimeUtc(secondCached.Path, DateTime.UtcNow.AddHours(-2));

        var firstHit = await cache.EnsureAsync(firstAuthorization);
        var thirdCached = await cache.EnsureAsync(thirdAuthorization);

        Assert.Equal(firstCached.Path, firstHit.Path);
        Assert.Equal(3, calls);
        Assert.True(File.Exists(firstCached.Path));
        Assert.False(File.Exists(secondCached.Path));
        Assert.True(File.Exists(thirdCached.Path));
        Assert.True(TotalCacheBytes(temp.Path) <= 300_000);
    }

    [Fact]
    public async Task StalePartialIsDisposableButRecoveryEvidenceOutsideCacheIsUntouched()
    {
        using var temp = new TemporaryDirectory();
        const int maximumCacheBytes = 200_000;
        var staleHash = Convert.ToHexString(SHA256.HashData(CreatePayload(10, seed: 5)));
        var stalePartial = CachePath(temp.Path, staleHash) + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(stalePartial)!);
        await File.WriteAllBytesAsync(stalePartial, CreatePayload(150_000, seed: 6));
        File.SetLastWriteTimeUtc(stalePartial, DateTime.UtcNow.AddDays(-1));

        var recoveryEvidence = Path.Combine(temp.Path, "recovery", "do-not-delete.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(recoveryEvidence)!);
        await File.WriteAllBytesAsync(recoveryEvidence, CreatePayload(300_000, seed: 7));

        var target = CreatePayload(100_000, seed: 8);
        using var client = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(Bytes(target))));
        var cache = CreateCache(temp.Path, client, maximumCacheBytes);

        var cached = await cache.EnsureAsync(Authorization("target", target));

        Assert.False(File.Exists(stalePartial));
        Assert.True(File.Exists(cached.Path));
        Assert.True(File.Exists(recoveryEvidence));
        Assert.Equal(300_000, new FileInfo(recoveryEvidence).Length);
        Assert.True(TotalCacheBytes(temp.Path) <= maximumCacheBytes);
    }

    private static VerifiedPackageCache CreateCache(
        string root,
        HttpClient client,
        long maximumCacheBytes)
        => new(
            root,
            client,
            new VerifiedPackageCacheOptions(
                minimumFreeSpaceReserveBytes: 0,
                copyBufferBytes: 64 * 1024,
                maximumCacheBytes: maximumCacheBytes));

    private static AuthorizedPackageDownload Authorization(string name, byte[] bytes)
        => new(
            new Uri($"https://objects.example/{name}"),
            new Dictionary<string, string>(),
            DateTimeOffset.UtcNow.AddMinutes(10),
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)));

    private static HttpResponseMessage Bytes(byte[] bytes)
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };

    private static long TotalCacheBytes(string root)
    {
        var cacheRoot = Path.Combine(root, "packages", "sha256");
        if (!Directory.Exists(cacheRoot))
        {
            return 0;
        }

        return Directory.GetFiles(cacheRoot, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".package", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            .Sum(path => new FileInfo(path).Length);
    }

    private static string CachePath(string root, string hash)
    {
        var normalized = hash.ToLowerInvariant();
        return Path.Combine(
            root,
            "packages",
            "sha256",
            normalized[..2],
            normalized + ".package");
    }

    private static byte[] CreatePayload(int byteCount, int seed)
    {
        var bytes = new byte[byteCount];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)((index * 29 + seed * 17) % 251);
        }

        return bytes;
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
