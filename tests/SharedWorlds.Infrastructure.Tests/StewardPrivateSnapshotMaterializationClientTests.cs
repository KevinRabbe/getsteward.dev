using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class StewardPrivateSnapshotMaterializationClientTests
{
    [Fact]
    public async Task AuthorizedPlanPreservesExactRecordsAndUsesVerifiedCache()
    {
        var fixture = Fixture.Create();
        var directCalls = 0;
        using var api = new HttpClient(new DelegateHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                $"/api/v1/private-worlds/{fixture.WorldId.Value:D}/materialization-download",
                request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("access-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(Json(
                HttpStatusCode.OK,
                Envelope(
                    "PrivateSnapshotMaterializationDownloadAuthorized",
                    fixture.AuthorizedData())));
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
                Content = new ByteArrayContent(fixture.Bytes)
            });
        }));
        using var cacheRoot = new TemporaryDirectory();
        var client = CreateClient(api, transfer, cacheRoot.Path);

        var first = Assert.IsType<RemoteVerifiedPrivateSnapshotMaterialization>(
            await client.EnsureDownloadedAsync(fixture.WorldId));
        var second = Assert.IsType<RemoteVerifiedPrivateSnapshotMaterialization>(
            await client.EnsureDownloadedAsync(fixture.WorldId));

        Assert.Equal(fixture.WorldId, first.Plan.WorldId);
        Assert.Equal(fixture.State, first.Plan.StateRevision);
        Assert.Equal(fixture.Environment.Id, first.Plan.EnvironmentRevision.Id);
        Assert.Equal(fixture.Environment.WorldId, first.Plan.EnvironmentRevision.WorldId);
        Assert.Equal(
            fixture.Environment.ParentRevisionId,
            first.Plan.EnvironmentRevision.ParentRevisionId);
        Assert.Equal(fixture.Environment.CreatedAt, first.Plan.EnvironmentRevision.CreatedAt);
        Assert.Equal(fixture.Environment.CreatedBy, first.Plan.EnvironmentRevision.CreatedBy);
        Assert.Equal(
            fixture.Environment.Manifest.SchemaVersion,
            first.Plan.EnvironmentRevision.Manifest.SchemaVersion);
        Assert.Equal(
            fixture.Environment.Manifest.AdapterId,
            first.Plan.EnvironmentRevision.Manifest.AdapterId);
        Assert.Equal(
            fixture.Environment.Manifest.GameVersion,
            first.Plan.EnvironmentRevision.Manifest.GameVersion);
        Assert.Empty(first.Plan.EnvironmentRevision.Manifest.Components);
        Assert.Empty(first.Plan.EnvironmentRevision.Manifest.Configuration);
        Assert.Equal(fixture.State.Id, first.Plan.StateRevisionId);
        Assert.Equal(fixture.Environment.Id, first.Plan.EnvironmentRevisionId);
        Assert.Equal(fixture.Sha256, first.Plan.ExpectedSha256);
        Assert.True(File.Exists(first.Package.Path));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(first.Package.Path));
        Assert.Equal(first.Package.Path, second.Package.Path);
        Assert.Equal(1, directCalls);
    }

    [Fact]
    public async Task ContradictoryRevisionRecordFailsBeforeDirectTransfer()
    {
        var fixture = Fixture.Create();
        var contradictory = fixture.State with { Id = RevisionId.New() };
        using var api = new HttpClient(new DelegateHandler(_ => Task.FromResult(Json(
            HttpStatusCode.OK,
            Envelope(
                "PrivateSnapshotMaterializationDownloadAuthorized",
                fixture.AuthorizedData(stateRevision: contradictory))))))
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
            client.EnsureDownloadedAsync(fixture.WorldId));

        Assert.Equal(0, directCalls);
    }

    [Fact]
    public async Task ForbiddenAuthorizationHeaderFailsBeforeDirectTransfer()
    {
        var fixture = Fixture.Create();
        using var api = new HttpClient(new DelegateHandler(_ => Task.FromResult(Json(
            HttpStatusCode.OK,
            Envelope(
                "PrivateSnapshotMaterializationDownloadAuthorized",
                fixture.AuthorizedData(new Dictionary<string, string>
                {
                    ["Range"] = "bytes=0-3"
                }))))))
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
            client.EnsureDownloadedAsync(fixture.WorldId));

        Assert.Equal(0, directCalls);
    }

    [Fact]
    public async Task UnavailableReturnsReasonWithoutDirectTransfer()
    {
        var fixture = Fixture.Create();
        using var api = new HttpClient(new DelegateHandler(_ => Task.FromResult(Json(
            HttpStatusCode.Conflict,
            Envelope(
                "PrivateSnapshotMaterializationUnavailable",
                new { reason = "Exact revision evidence is not available yet." },
                retryable: true)))))
        {
            BaseAddress = new Uri("https://steward.example/")
        };
        using var transfer = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("Direct transfer must not start.")));
        using var cacheRoot = new TemporaryDirectory();
        var client = CreateClient(api, transfer, cacheRoot.Path);

        var result = await client.AuthorizeAsync(fixture.WorldId);

        Assert.Equal(RemotePrivateSnapshotMaterializationStatus.Unavailable, result.Status);
        Assert.Null(result.Plan);
        Assert.Equal("Exact revision evidence is not available yet.", result.Reason);
        Assert.Null(await client.EnsureDownloadedAsync(fixture.WorldId));
    }

    [Theory]
    [InlineData(
        HttpStatusCode.NotFound,
        "PrivateSnapshotNotFound",
        RemotePrivateSnapshotMaterializationStatus.NotFoundOrUnauthorized)]
    [InlineData(
        HttpStatusCode.Conflict,
        "PrivateSnapshotAlreadyHere",
        RemotePrivateSnapshotMaterializationStatus.AlreadyHere)]
    [InlineData(
        HttpStatusCode.Conflict,
        "PrivateSnapshotHeadConflict",
        RemotePrivateSnapshotMaterializationStatus.Conflict)]
    [InlineData(
        HttpStatusCode.Conflict,
        "PrivateSnapshotStorageIntegrityFailure",
        RemotePrivateSnapshotMaterializationStatus.StorageIntegrityFailure)]
    public async Task MapsExactTerminalStatuses(
        HttpStatusCode httpStatus,
        string code,
        RemotePrivateSnapshotMaterializationStatus expected)
    {
        var fixture = Fixture.Create();
        var data = httpStatus == HttpStatusCode.NotFound
            ? null
            : new { reason = "Terminal reason." };
        using var api = new HttpClient(new DelegateHandler(_ => Task.FromResult(Json(
            httpStatus,
            Envelope(code, data)))))
        {
            BaseAddress = new Uri("https://steward.example/")
        };
        using var transfer = new HttpClient(new DelegateHandler(_ =>
            throw new InvalidOperationException("Direct transfer must not start.")));
        using var cacheRoot = new TemporaryDirectory();
        var client = CreateClient(api, transfer, cacheRoot.Path);

        var result = await client.AuthorizeAsync(fixture.WorldId);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Plan);
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
        var client = new StewardPrivateSnapshotMaterializationClient(
            api,
            cache,
            _ => Task.FromResult<string?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.AuthorizeAsync(WorldId.New()));

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
            new StewardPrivateSnapshotMaterializationClient(
                api,
                cache,
                _ => Task.FromResult<string?>("token")));
    }

    private static StewardPrivateSnapshotMaterializationClient CreateClient(
        HttpClient api,
        HttpClient transfer,
        string cacheRoot)
        => new(
            api,
            CreateCache(cacheRoot, transfer),
            _ => Task.FromResult<string?>("access-token"));

    private static VerifiedPackageCache CreateCache(
        string root,
        HttpClient transfer)
        => new(
            root,
            transfer,
            new VerifiedPackageCacheOptions(
                minimumFreeSpaceReserveBytes: 0,
                copyBufferBytes: 64 * 1024,
                maximumCacheBytes: 1024 * 1024,
                transferInactivityTimeout: TimeSpan.FromSeconds(5)));

    private static object Envelope(
        string code,
        object? data = null,
        bool retryable = false)
        => new { code, retryable, data };

    private static HttpResponseMessage Json(
        HttpStatusCode statusCode,
        object value)
        => new(statusCode)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value),
                Encoding.UTF8,
                "application/json")
        };

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes));

    private static EnvironmentManifest Manifest()
        => new(
            SchemaVersion: 1,
            AdapterId: "factorio",
            GameVersion: "2.0.0",
            Components: [],
            Configuration: new Dictionary<string, string>());

    private sealed record Fixture(
        WorldId WorldId,
        StateRevision State,
        EnvironmentRevision Environment,
        EnvironmentManifest Manifest,
        byte[] Bytes,
        string Sha256)
    {
        public static Fixture Create()
        {
            var worldId = WorldId.New();
            var environmentId = RevisionId.New();
            var manifest = StewardPrivateSnapshotMaterializationClientTests.Manifest();
            var environment = new EnvironmentRevision(
                environmentId,
                worldId,
                ParentRevisionId: RevisionId.New(),
                CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-2),
                CreatedBy: null,
                manifest);
            var state = new StateRevision(
                RevisionId.New(),
                worldId,
                ParentRevisionId: RevisionId.New(),
                CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
                CreatedBy: null,
                AdapterId: "factorio",
                StatePackageId: "state-package-id",
                EnvironmentRevisionId: environmentId);
            var bytes = new byte[] { 7, 8, 9, 10 };
            return new(
                worldId,
                state,
                environment,
                manifest,
                bytes,
                Hash(bytes));
        }

        public object AuthorizedData(
            IReadOnlyDictionary<string, string>? headers = null,
            StateRevision? stateRevision = null)
            => new
            {
                worldId = WorldId.Value,
                sourceInstallationId = "pc-a",
                stateRevisionId = State.Id.Value,
                environmentRevisionId = Environment.Id.Value,
                gameAdapterId = "factorio",
                expectedByteSize = Bytes.LongLength,
                expectedSha256 = Sha256,
                environmentManifest = Manifest,
                stateRevision = stateRevision ?? State,
                environmentRevision = Environment,
                authorization = new
                {
                    uri = "https://objects.example/private-materialization",
                    method = "GET",
                    requiredHeaders = headers ?? new Dictionary<string, string>
                    {
                        ["x-private"] = "snapshot"
                    },
                    expiresAt = DateTimeOffset.UtcNow.AddHours(1),
                    expectedByteSize = Bytes.LongLength
                }
            };
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public DelegateHandler(
            Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
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
                "steward-private-materialization-client-tests",
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
