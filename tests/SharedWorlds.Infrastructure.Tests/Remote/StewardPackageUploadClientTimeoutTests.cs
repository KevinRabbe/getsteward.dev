using System.Net;
using System.Security.Cryptography;
using System.Text;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardPackageUploadClientTimeoutTests
{
    private static readonly TimeSpan TestPartTimeout = TimeSpan.FromMilliseconds(200);

    [Fact]
    public async Task StalledObjectStoragePartTimesOutWithoutFinalizingTransfer()
    {
        var worldId = WorldId.New();
        var revisionId = RevisionId.New();
        var transferId = Guid.NewGuid();
        var bytes = Encoding.ASCII.GetBytes("abcd");
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        var apiPaths = new List<string>();
        using var apiHttp = CreateApiClient(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            apiPaths.Add(path);
            return path switch
            {
                var value when value == $"/api/v1/worlds/{worldId.Value:D}/transfers" => JsonResponse(
                    HttpStatusCode.Created,
                    TransferStartedJson(transferId, worldId, revisionId, bytes.Length, sha256)),
                var value when value == $"/api/v1/transfers/{transferId:D}" => JsonResponse(
                    HttpStatusCode.OK,
                    ProgressJson(transferId, worldId, revisionId, bytes.Length, sha256)),
                var value when value == $"/api/v1/transfers/{transferId:D}/parts/1/authorization" => JsonResponse(
                    HttpStatusCode.OK,
                    PartAuthorizationJson(bytes.Length)),
                _ => throw new InvalidOperationException($"Unexpected API route {request.RequestUri}.")
            };
        });
        var storageCalls = 0;
        using var storageHttp = new HttpClient(new AsyncDelegateHandler(async (_, cancellationToken) =>
        {
            storageCalls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after cancellation.");
        }));
        var client = new StewardPackageUploadClient(apiHttp, storageHttp, TestPartTimeout);
        await using var package = new MemoryStream(bytes, writable: false);

        var exception = await Assert.ThrowsAsync<RemotePackagePartUploadTimeoutException>(() => client.UploadAsync(
            worldId,
            revisionId,
            RemotePackageKind.State,
            package,
            requiredEnvironmentRevisionId: null,
            "access-token"));

        Assert.Equal(TestPartTimeout, exception.PartUploadTimeout);
        Assert.Equal(1, storageCalls);
        Assert.Equal(3, apiPaths.Count);
        Assert.DoesNotContain(apiPaths, path => path.EndsWith("/finalize", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CallerCancellationIsNotRelabeledAsPartTimeout()
    {
        var worldId = WorldId.New();
        var revisionId = RevisionId.New();
        var transferId = Guid.NewGuid();
        var bytes = Encoding.ASCII.GetBytes("abcd");
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        using var apiHttp = CreateApiClient(request => request.RequestUri!.AbsolutePath switch
        {
            var value when value == $"/api/v1/worlds/{worldId.Value:D}/transfers" => JsonResponse(
                HttpStatusCode.Created,
                TransferStartedJson(transferId, worldId, revisionId, bytes.Length, sha256)),
            var value when value == $"/api/v1/transfers/{transferId:D}" => JsonResponse(
                HttpStatusCode.OK,
                ProgressJson(transferId, worldId, revisionId, bytes.Length, sha256)),
            var value when value == $"/api/v1/transfers/{transferId:D}/parts/1/authorization" => JsonResponse(
                HttpStatusCode.OK,
                PartAuthorizationJson(bytes.Length)),
            _ => throw new InvalidOperationException($"Unexpected API route {request.RequestUri}.")
        });
        using var storageHttp = new HttpClient(new AsyncDelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable after cancellation.");
        }));
        var client = new StewardPackageUploadClient(apiHttp, storageHttp, TimeSpan.FromSeconds(10));
        await using var package = new MemoryStream(bytes, writable: false);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var exception = await Record.ExceptionAsync(() => client.UploadAsync(
            worldId,
            revisionId,
            RemotePackageKind.State,
            package,
            requiredEnvironmentRevisionId: null,
            "access-token",
            cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.IsNotType<RemotePackagePartUploadTimeoutException>(exception);
    }

    [Fact]
    public void InfinitePartTimeoutIsRejected()
    {
        using var apiHttp = CreateApiClient(_ => throw new InvalidOperationException());
        using var storageHttp = new HttpClient(new AsyncDelegateHandler((_, _) =>
            throw new InvalidOperationException()));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new StewardPackageUploadClient(apiHttp, storageHttp, Timeout.InfiniteTimeSpan));
    }

    private static HttpClient CreateApiClient(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        => new(new AsyncDelegateHandler((request, _) => Task.FromResult(responseFactory(request))))
        {
            BaseAddress = new Uri("https://steward.test/")
        };

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string TransferStartedJson(
        Guid transferId,
        WorldId worldId,
        RevisionId revisionId,
        int bytes,
        string sha256)
        => $$"""
        {
          "code": "TransferStarted",
          "data": {
            "transferId": "{{transferId:D}}",
            "worldId": "{{worldId.Value:D}}",
            "revisionId": "{{revisionId.Value:D}}",
            "kind": "State",
            "expectedByteSize": {{bytes}},
            "expectedSha256": "{{sha256}}",
            "requiredEnvironmentRevisionId": null,
            "partSizeBytes": {{bytes}},
            "partCount": 1,
            "expiresAt": "{{FutureExpiration()}}",
            "state": "Active"
          },
          "retryable": false
        }
        """;

    private static string ProgressJson(
        Guid transferId,
        WorldId worldId,
        RevisionId revisionId,
        int bytes,
        string sha256)
        => $$"""
        {
          "code": "TransferProgress",
          "data": {
            "transfer": {
              "transferId": "{{transferId:D}}",
              "worldId": "{{worldId.Value:D}}",
              "revisionId": "{{revisionId.Value:D}}",
              "kind": "State",
              "expectedByteSize": {{bytes}},
              "expectedSha256": "{{sha256}}",
              "requiredEnvironmentRevisionId": null,
              "partSizeBytes": {{bytes}},
              "partCount": 1,
              "expiresAt": "{{FutureExpiration()}}",
              "state": "Active"
            },
            "completedParts": [],
            "providerUploadCompleted": false
          },
          "retryable": false
        }
        """;

    private static string PartAuthorizationJson(int bytes)
        => $$"""
        {
          "code": "PartAuthorized",
          "data": {
            "uri": "https://storage.test/part-1",
            "method": "PUT",
            "requiredHeaders": {},
            "expiresAt": "{{FutureExpiration()}}",
            "expectedByteSize": {{bytes}}
          },
          "retryable": false
        }
        """;

    private static string FutureExpiration()
        => DateTimeOffset.UtcNow.AddMinutes(30).ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class AsyncDelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public AsyncDelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}
