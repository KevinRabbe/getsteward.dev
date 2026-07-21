using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class VerifiedPackageCacheTests
{
    [Fact]
    public async Task ValidPackageIsPublishedOnceAndReusedWithoutNetwork()
    {
        using var temp = new TemporaryDirectory();
        var bytes = CreatePayload(220_000);
        var hash = Sha256(bytes);
        var calls = 0;
        using var transferClient = new HttpClient(new DelegateHandler((request, _) =>
        {
            calls++;
            Assert.Equal("required-value", request.Headers.GetValues("x-storage-test").Single());
            return Task.FromResult(Bytes(HttpStatusCode.OK, bytes));
        }));
        var cache = CreateCache(temp.Path, transferClient);
        var authorization = Authorization(
            hash,
            bytes.LongLength,
            new Dictionary<string, string> { ["x-storage-test"] = "required-value" });

        var first = await cache.EnsureAsync(authorization);
        var second = await cache.EnsureAsync(authorization);

        Assert.Equal(1, calls);
        Assert.Equal(first, second);
        Assert.True(File.Exists(first.Path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(first.Path));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task IncompleteDownloadRemainsPartialAndResumesWithRange()
    {
        using var temp = new TemporaryDirectory();
        var bytes = CreatePayload(240_000);
        var hash = Sha256(bytes);
        const int prefixLength = 90_000;
        var calls = 0;
        using var transferClient = new HttpClient(new DelegateHandler((request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Null(request.Headers.Range);
                return Task.FromResult(Bytes(HttpStatusCode.OK, bytes[..prefixLength]));
            }

            Assert.Equal(prefixLength, request.Headers.Range?.Ranges.Single().From);
            var response = Bytes(HttpStatusCode.PartialContent, bytes[prefixLength..]);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
                prefixLength,
                bytes.Length - 1,
                bytes.Length);
            return Task.FromResult(response);
        }));
        var cache = CreateCache(temp.Path, transferClient);

        await Assert.ThrowsAsync<IncompletePackageDownloadException>(() =>
            cache.EnsureAsync(Authorization(hash, bytes.LongLength)));
        var partial = Assert.Single(Directory.GetFiles(temp.Path, "*.partial", SearchOption.AllDirectories));
        Assert.Equal(prefixLength, new FileInfo(partial).Length);

        var completed = await cache.EnsureAsync(Authorization(hash, bytes.LongLength));

        Assert.Equal(2, calls);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(completed.Path));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task RangeIgnoredByProviderRestartsFromZeroInsteadOfAppending()
    {
        using var temp = new TemporaryDirectory();
        var bytes = CreatePayload(180_000);
        var hash = Sha256(bytes);
        const int prefixLength = 70_000;
        var calls = 0;
        using var transferClient = new HttpClient(new DelegateHandler((request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult(Bytes(HttpStatusCode.OK, bytes[..prefixLength]));
            }

            Assert.Equal(prefixLength, request.Headers.Range?.Ranges.Single().From);
            return Task.FromResult(Bytes(HttpStatusCode.OK, bytes));
        }));
        var cache = CreateCache(temp.Path, transferClient);

        await Assert.ThrowsAsync<IncompletePackageDownloadException>(() =>
            cache.EnsureAsync(Authorization(hash, bytes.LongLength)));
        var completed = await cache.EnsureAsync(Authorization(hash, bytes.LongLength));

        Assert.Equal(2, calls);
        Assert.Equal(bytes.LongLength, new FileInfo(completed.Path).Length);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(completed.Path));
    }

    [Fact]
    public async Task HashMismatchNeverPublishesFinalCacheEntry()
    {
        using var temp = new TemporaryDirectory();
        var expected = CreatePayload(160_000);
        var wrong = expected.ToArray();
        wrong[^1] ^= 0x7F;
        using var transferClient = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(Bytes(HttpStatusCode.OK, wrong))));
        var cache = CreateCache(temp.Path, transferClient);

        await Assert.ThrowsAsync<PackageIntegrityException>(() =>
            cache.EnsureAsync(Authorization(Sha256(expected), expected.LongLength)));

        Assert.Empty(Directory.GetFiles(temp.Path, "*.package", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.partial", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task StewardBearerTokenNeverReachesObjectStorageClient()
    {
        using var temp = new TemporaryDirectory();
        var bytes = CreatePayload(130_000);
        var hash = Sha256(bytes);
        const string accessToken = "steward-access-token";
        AuthenticationHeaderValue? apiAuthorization = null;
        AuthenticationHeaderValue? storageAuthorization = null;

        using var apiClient = new HttpClient(new DelegateHandler((request, _) =>
        {
            apiAuthorization = request.Headers.Authorization;
            var json = $$"""
                {
                  "code": "DownloadAuthorized",
                  "data": {
                    "authorization": {
                      "uri": "https://objects.example/package",
                      "method": "GET",
                      "requiredHeaders": { "x-object-token": "scoped-storage-value" },
                      "expiresAt": "{{DateTimeOffset.UtcNow.AddMinutes(10):O}}",
                      "expectedByteSize": {{bytes.LongLength}}
                    },
                    "expectedByteSize": {{bytes.LongLength}},
                    "expectedSha256": "{{hash}}"
                  },
                  "retryable": false
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }))
        {
            BaseAddress = new Uri("https://api.example/")
        };

        using var transferClient = new HttpClient(new DelegateHandler((request, _) =>
        {
            storageAuthorization = request.Headers.Authorization;
            Assert.Equal("scoped-storage-value", request.Headers.GetValues("x-object-token").Single());
            return Task.FromResult(Bytes(HttpStatusCode.OK, bytes));
        }));

        var source = new StewardVerifiedPackageSource(
            new StewardPackageDownloadClient(apiClient),
            CreateCache(temp.Path, transferClient));

        await using var stream = await source.OpenStateRevisionAsync(
            WorldId.New(),
            RevisionId.New(),
            accessToken);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);

        Assert.Equal("Bearer", apiAuthorization?.Scheme);
        Assert.Equal(accessToken, apiAuthorization?.Parameter);
        Assert.Null(storageAuthorization);
        Assert.Equal(bytes, memory.ToArray());
    }

    [Fact]
    public async Task MachineReadableApiFailureIsPreservedForClientPolicy()
    {
        using var apiClient = new HttpClient(new DelegateHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(
                    "{\"code\":\"StorageIntegrityFailure\",\"retryable\":false}",
                    Encoding.UTF8,
                    "application/json")
            })))
        {
            BaseAddress = new Uri("https://api.example/")
        };
        var client = new StewardPackageDownloadClient(apiClient);

        var exception = await Assert.ThrowsAsync<StewardRemoteApiException>(() =>
            client.AuthorizeDownloadAsync(
                WorldId.New(),
                RevisionId.New(),
                RemotePackageKind.State,
                "token"));

        Assert.Equal(HttpStatusCode.Conflict, exception.StatusCode);
        Assert.Equal("StorageIntegrityFailure", exception.Code);
        Assert.False(exception.Retryable);
    }

    private static VerifiedPackageCache CreateCache(string root, HttpClient client)
        => new(
            root,
            client,
            new VerifiedPackageCacheOptions(
                minimumFreeSpaceReserveBytes: 0,
                copyBufferBytes: 64 * 1024));

    private static AuthorizedPackageDownload Authorization(
        string hash,
        long byteSize,
        IReadOnlyDictionary<string, string>? headers = null)
        => new(
            new Uri("https://objects.example/package"),
            headers ?? new Dictionary<string, string>(),
            DateTimeOffset.UtcNow.AddMinutes(10),
            byteSize,
            hash);

    private static HttpResponseMessage Bytes(HttpStatusCode statusCode, byte[] bytes)
        => new(statusCode)
        {
            Content = new ByteArrayContent(bytes)
        };

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] CreatePayload(int byteCount)
    {
        var bytes = new byte[byteCount];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)((index * 29 + 11) % 251);
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
                // Test cleanup only.
            }
            catch (UnauthorizedAccessException)
            {
                // Test cleanup only.
            }
        }
    }
}
