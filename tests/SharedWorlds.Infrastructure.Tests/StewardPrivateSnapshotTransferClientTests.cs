using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class StewardPrivateSnapshotTransferClientTests
{
    [Fact]
    public async Task ResumesMissingPartsAndRequiresExactFinalSnapshotMetadata()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var transferId = Guid.NewGuid();
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6 };
        var sha256 = Hash(bytes);
        var manifest = Manifest();
        var authorizedAt = DateTimeOffset.UtcNow.AddHours(1);
        var apiRequests = new List<HttpRequestMessage>();
        var directUploads = new List<byte[]>();
        using var api = new HttpClient(new DelegateHandler(async request =>
        {
            apiRequests.Add(CloneRequest(request));
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("access-token", request.Headers.Authorization?.Parameter);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/snapshot-upload", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, Envelope(
                    "PrivateSnapshotUploadStarted",
                    TransferData(
                        transferId,
                        worldId,
                        stateId,
                        environmentId,
                        bytes.LongLength,
                        sha256,
                        completedParts: [])));
            }

            if (path == $"/api/v1/private-snapshot-transfers/{transferId:D}" &&
                request.Method == HttpMethod.Get)
            {
                return Json(HttpStatusCode.OK, Envelope(
                    "PrivateSnapshotTransferProgress",
                    TransferData(
                        transferId,
                        worldId,
                        stateId,
                        environmentId,
                        bytes.LongLength,
                        sha256,
                        completedParts: [new { partNumber = 1, byteSize = 4L }])));
            }

            if (path.EndsWith("/parts/2/authorization", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, Envelope(
                    "PrivateSnapshotPartAuthorized",
                    new
                    {
                        authorization = new
                        {
                            uri = "https://objects.example/upload/part-2",
                            method = "PUT",
                            requiredHeaders = new Dictionary<string, string>
                            {
                                ["x-snapshot"] = "part-2"
                            },
                            expiresAt = authorizedAt,
                            expectedByteSize = 2L
                        }
                    }));
            }

            if (path.EndsWith("/finalize", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, Envelope(
                    "PrivateSnapshotFinalized",
                    new
                    {
                        worldId = worldId.Value,
                        sourceInstallationId = "pc-a",
                        stateRevisionId = stateId.Value,
                        environmentRevisionId = environmentId.Value,
                        gameAdapterId = "factorio",
                        expectedByteSize = bytes.LongLength,
                        expectedSha256 = sha256,
                        environmentManifest = manifest,
                        publishedAt = DateTimeOffset.UtcNow
                    }));
            }

            return Json(HttpStatusCode.NotFound, Envelope("UnexpectedRoute"));
        }))
        {
            BaseAddress = new Uri("https://steward.example/")
        };
        using var transfer = new HttpClient(new DelegateHandler(async request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.True(request.Headers.TryGetValues("x-snapshot", out var values));
            Assert.Equal("part-2", Assert.Single(values));
            directUploads.Add(await request.Content!.ReadAsByteArrayAsync());
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var cacheRoot = new TemporaryDirectory();
        var client = CreateClient(api, transfer, cacheRoot.Path);
        await using var package = new MemoryStream(bytes);

        var result = await client.UploadAsync(
            worldId,
            stateId,
            environmentId,
            "factorio",
            manifest,
            package);

        Assert.Equal(RemotePrivateSnapshotUploadStatus.Published, result.Status);
        Assert.Equal(transferId, result.TransferId);
        Assert.Equal(sha256, result.Sha256);
        Assert.Equal(new byte[] { 5, 6 }, Assert.Single(directUploads));
        Assert.Equal(4, apiRequests.Count);
        Assert.DoesNotContain(apiRequests, request =>
            request.RequestUri!.AbsolutePath.EndsWith("/parts/1/authorization", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MismatchedProgressFailsBeforeDirectObjectTransfer()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var transferId = Guid.NewGuid();
        var bytes = new byte[] { 1, 2, 3, 4 };
        var sha256 = Hash(bytes);
        var apiCall = 0;
        using var api = new HttpClient(new DelegateHandler(request =>
        {
            apiCall++;
            var data = TransferData(
                transferId,
                apiCall == 1 ? worldId : WorldId.New(),
                stateId,
                environmentId,
                bytes.LongLength,
                sha256,
                completedParts: []);
            return Task.FromResult(Json(
                HttpStatusCode.OK,
                Envelope(
                    apiCall == 1
                        ? "PrivateSnapshotUploadStarted"
                        : "PrivateSnapshotTransferProgress",
                    data)));
        }))
        {
            BaseAddress = new Uri("https://steward.example/")
        };
        var directCalls = 0;
        using var transfer = new HttpClient(new DelegateHandler(request =>
        {
            directCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        using var cacheRoot = new TemporaryDirectory();
        var client = CreateClient(api, transfer, cacheRoot.Path);
        await using var package = new MemoryStream(bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.UploadAsync(
            worldId,
            stateId,
            environmentId,
            "factorio",
            Manifest(),
            package));

        Assert.Equal(0, directCalls);
    }

    [Fact]
    public async Task DuplicateCompletedPartEvidenceFailsClosed()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var transferId = Guid.NewGuid();
        var bytes = new byte[] { 1, 2, 3, 4 };
        var sha256 = Hash(bytes);
        var apiCall = 0;
        using var api = new HttpClient(new DelegateHandler(request =>
        {
            apiCall++;
            var completed = apiCall == 1
                ? Array.Empty<object>()
                :
                [
                    new { partNumber = 1, byteSize = 4L },
                    new { partNumber = 1, byteSize = 4L }
                ];
            return Task.FromResult(Json(
                HttpStatusCode.OK,
                Envelope(
                    apiCall == 1
                        ? "PrivateSnapshotUploadStarted"
                        : "PrivateSnapshotTransferProgress",
                    TransferData(
                        transferId,
                        worldId,
                        stateId,
                        environmentId,
                        bytes.LongLength,
                        sha256,
                        completed))));
        }))
        {
            BaseAddress = new Uri("https://steward.example/")
        };
        using var transfer = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("Direct transfer must not start.")));
        using var cacheRoot = new TemporaryDirectory();
        var client = CreateClient(api, transfer, cacheRoot.Path);
        await using var package = new MemoryStream(bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => client.UploadAsync(
            worldId,
            stateId,
            environmentId,
            "factorio",
            Manifest(),
            package));
    }

    [Fact]
    public async Task AuthorizedDownloadUsesVerifiedCacheAndExactPlanIdentity()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var bytes = new byte[] { 7, 8, 9, 10 };
        var sha256 = Hash(bytes);
        var manifest = Manifest();
        var directCalls = 0;
        using var api = new HttpClient(new DelegateHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                $"/api/v1/private-worlds/{worldId.Value:D}/snapshot-download",
                request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(HttpStatusCode.OK, Envelope(
                "PrivateSnapshotDownloadAuthorized",
                new
                {
                    worldId = worldId.Value,
                    sourceInstallationId = "pc-a",
                    stateRevisionId = stateId.Value,
                    environmentRevisionId = environmentId.Value,
                    gameAdapterId = "factorio",
                    expectedByteSize = bytes.LongLength,
                    expectedSha256 = sha256,
                    environmentManifest = manifest,
                    authorization = new
                    {
                        uri = "https://objects.example/private-download",
                        method = "GET",
                        requiredHeaders = new Dictionary<string, string>
                        {
                            ["x-private"] = "snapshot"
                        },
                        expiresAt = DateTimeOffset.UtcNow.AddHours(1),
                        expectedByteSize = bytes.LongLength
                    }
                })));
        }))
        {
            BaseAddress = new Uri("https://steward.example/")
        };
        using var transfer = new HttpClient(new DelegateHandler(request =>
        {
            directCalls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.True(request.Headers.TryGetValues("x-private", out var values));
            Assert.Equal("snapshot", Assert.Single(values));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
        }));
        using var cacheRoot = new TemporaryDirectory();
        var client = CreateClient(api, transfer, cacheRoot.Path);

        var first = Assert.IsType<RemoteVerifiedPrivateSnapshot>(
            await client.EnsureDownloadedAsync(worldId));
        var second = Assert.IsType<RemoteVerifiedPrivateSnapshot>(
            await client.EnsureDownloadedAsync(worldId));

        Assert.Equal(worldId, first.Plan.WorldId);
        Assert.Equal(stateId, first.Plan.StateRevisionId);
        Assert.Equal(environmentId, first.Plan.EnvironmentRevisionId);
        Assert.Equal(sha256, first.Plan.ExpectedSha256);
        Assert.True(File.Exists(first.Package.Path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(first.Package.Path));
        Assert.Equal(first.Package.Path, second.Package.Path);
        Assert.Equal(1, directCalls);
    }

    [Fact]
    public async Task ContradictoryDownloadPlanFailsBeforeObjectTransfer()
    {
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var bytes = new byte[] { 1, 2, 3 };
        using var api = new HttpClient(new DelegateHandler(_ => Task.FromResult(Json(
            HttpStatusCode.OK,
            Envelope(
                "PrivateSnapshotDownloadAuthorized",
                new
                {
                    worldId = WorldId.New().Value,
                    sourceInstallationId = "pc-a",
                    stateRevisionId = stateId.Value,
                    environmentRevisionId = environmentId.Value,
                    gameAdapterId = "factorio",
                    expectedByteSize = bytes.LongLength,
                    expectedSha256 = Hash(bytes),
                    environmentManifest = Manifest(),
                    authorization = new
                    {
                        uri = "https://objects.example/private-download",
                        method = "GET",
                        requiredHeaders = new Dictionary<string, string>(),
                        expiresAt = DateTimeOffset.UtcNow.AddHours(1),
                        expectedByteSize = bytes.LongLength
                    }
                })))))
        {
            BaseAddress = new Uri("https://steward.example/")
        };
        var directCalls = 0;
        using var transfer = new HttpClient(new DelegateHandler(_ =>
        {
            directCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        using var cacheRoot = new TemporaryDirectory();
        var client = CreateClient(api, transfer, cacheRoot.Path);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.EnsureDownloadedAsync(worldId));
        Assert.Equal(0, directCalls);
    }

    [Fact]
    public async Task UnavailableDownloadReturnsReasonWithoutDirectTransfer()
    {
        var worldId = WorldId.New();
        using var api = new HttpClient(new DelegateHandler(_ => Task.FromResult(Json(
            HttpStatusCode.Conflict,
            Envelope(
                "PrivateSnapshotUnavailable",
                new { reason = "Exact bytes are not uploaded yet." },
                retryable: true)))))
        {
            BaseAddress = new Uri("https://steward.example/")
        };
        using var transfer = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("Direct transfer must not start.")));
        using var cacheRoot = new TemporaryDirectory();
        var client = CreateClient(api, transfer, cacheRoot.Path);

        var result = await client.AuthorizeDownloadAsync(worldId);

        Assert.Equal(RemotePrivateSnapshotDownloadStatus.Unavailable, result.Status);
        Assert.Null(result.Plan);
        Assert.Equal("Exact bytes are not uploaded yet.", result.Reason);
        Assert.Null(await client.EnsureDownloadedAsync(worldId));
    }

    [Fact]
    public async Task MissingSessionFailsBeforeApiNetworkAccess()
    {
        var apiCalls = 0;
        using var api = new HttpClient(new DelegateHandler(_ =>
        {
            apiCalls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }))
        {
            BaseAddress = new Uri("https://steward.example/")
        };
        using var transfer = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("Direct transfer must not start.")));
        using var cacheRoot = new TemporaryDirectory();
        var cache = CreateCache(cacheRoot.Path, transfer);
        var client = new StewardPrivateSnapshotTransferClient(
            api,
            transfer,
            cache,
            _ => Task.FromResult<string?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.AuthorizeDownloadAsync(WorldId.New()));
        Assert.Equal(0, apiCalls);
    }

    [Fact]
    public void NonHttpsApiBaseAddressIsRejected()
    {
        using var api = new HttpClient
        {
            BaseAddress = new Uri("http://remote.example/")
        };
        using var transfer = new HttpClient();
        using var cacheRoot = new TemporaryDirectory();
        var cache = CreateCache(cacheRoot.Path, transfer);

        Assert.Throws<ArgumentException>(() =>
            new StewardPrivateSnapshotTransferClient(
                api,
                transfer,
                cache,
                _ => Task.FromResult<string?>("token")));
    }

    private static StewardPrivateSnapshotTransferClient CreateClient(
        HttpClient api,
        HttpClient transfer,
        string cacheRoot)
        => new(
            api,
            transfer,
            CreateCache(cacheRoot, transfer),
            _ => Task.FromResult<string?>("access-token"));

    private static VerifiedPackageCache CreateCache(string root, HttpClient transfer)
        => new(
            root,
            transfer,
            new VerifiedPackageCacheOptions(
                minimumFreeSpaceReserveBytes: 0,
                copyBufferBytes: 64 * 1024,
                maximumCacheBytes: 1024 * 1024,
                transferInactivityTimeout: TimeSpan.FromSeconds(5)));

    private static object TransferData(
        Guid transferId,
        WorldId worldId,
        RevisionId stateId,
        RevisionId environmentId,
        long byteSize,
        string sha256,
        object[] completedParts)
        => new
        {
            transferId,
            worldId = worldId.Value,
            stateRevisionId = stateId.Value,
            environmentRevisionId = environmentId.Value,
            gameAdapterId = "factorio",
            expectedByteSize = byteSize,
            expectedSha256 = sha256,
            partSizeBytes = 4,
            partCount = checked((int)((byteSize + 3) / 4)),
            createdAt = DateTimeOffset.UtcNow,
            expiresAt = DateTimeOffset.UtcNow.AddHours(1),
            state = 1,
            completedParts,
            providerUploadCompleted = false
        };

    private static object Envelope(
        string code,
        object? data = null,
        bool retryable = false)
        => new { code, retryable, data };

    private static HttpResponseMessage Json(HttpStatusCode statusCode, object value)
        => new(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value),
                Encoding.UTF8,
                "application/json")
        };

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        clone.Headers.Authorization = request.Headers.Authorization;
        return clone;
    }

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    private static EnvironmentManifest Manifest()
        => new(
            SchemaVersion: 1,
            AdapterId: "factorio",
            GameVersion: "2.0.0",
            Components: [],
            Configuration: new Dictionary<string, string>());

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "steward-private-client-tests",
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
