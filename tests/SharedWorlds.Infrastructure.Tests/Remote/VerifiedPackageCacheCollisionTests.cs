using System.Net;
using System.Security.Cryptography;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class VerifiedPackageCacheCollisionTests
{
    [Fact]
    public async Task CorruptCompetingFinalIsRejectedThenRecoveredOnNextAttempt()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "SharedWorlds.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = CreatePayload(150_000);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var normalized = hash.ToLowerInvariant();
            var finalPath = Path.Combine(
                root,
                "packages",
                "sha256",
                normalized[..2],
                normalized + ".package");
            var calls = 0;

            using var http = new HttpClient(new DelegateHandler((_, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                    var corrupt = bytes.ToArray();
                    corrupt[^1] ^= 0x55;
                    File.WriteAllBytes(finalPath, corrupt);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                });
            }));
            var cache = new VerifiedPackageCache(
                root,
                http,
                new VerifiedPackageCacheOptions(
                    minimumFreeSpaceReserveBytes: 0,
                    copyBufferBytes: 64 * 1024));
            var authorization = new AuthorizedPackageDownload(
                new Uri("https://objects.example/package"),
                new Dictionary<string, string>(),
                DateTimeOffset.UtcNow.AddMinutes(10),
                bytes.LongLength,
                hash);

            await Assert.ThrowsAsync<PackageIntegrityException>(() => cache.EnsureAsync(authorization));

            var recovered = await cache.EnsureAsync(authorization);

            Assert.Equal(2, calls);
            Assert.Equal(finalPath, recovered.Path);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(finalPath));
            Assert.Empty(Directory.GetFiles(root, "*.partial", SearchOption.AllDirectories));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
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

    private static byte[] CreatePayload(int byteCount)
    {
        var bytes = new byte[byteCount];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)((index * 37 + 19) % 251);
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
}
