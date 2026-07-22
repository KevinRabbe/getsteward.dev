using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardPackageUploadClientTests
{
    [Fact]
    public async Task UploadResumesCompletedPartsAndKeepsStewardCredentialOffObjectStorage()
    {
        var worldId = WorldId.New();
        var revisionId = RevisionId.New();
        var environmentId = RevisionId.New();
        var transferId = Guid.NewGuid();
        var bytes = Encoding.ASCII.GetBytes("abcdefghij");
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        var api = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path == $"/api/v1/worlds/{worldId.Value:D}/transfers" => JsonResponse(
                HttpStatusCode.Created,
                TransferStartedJson(transferId, worldId, revisionId, environmentId, bytes.Length, sha256, 4, 3)),
            var path when path == $"/api/v1/transfers/{transferId:D}" => JsonResponse(
                HttpStatusCode.OK,
                ProgressJson(transferId, worldId, revisionId, environmentId, bytes.Length, sha256, 4, 3,
                    completedPartNumber: 1,
                    completedPartBytes: 4)),
            var path when path == $"/api/v1/transfers/{transferId:D}/parts/2/authorization" => JsonResponse(
                HttpStatusCode.OK,
                PartAuthorizationJson("https://storage.test/part-2", 4, "two")),
            var path when path == $"/api/v1/transfers/{transferId:D}/parts/3/authorization" => JsonResponse(
                HttpStatusCode.OK,
                PartAuthorizationJson("https://storage.test/part-3", 2, "three")),
            var path when path == $"/api/v1/transfers/{transferId:D}/finalize" => JsonResponse(
                HttpStatusCode.OK,
                """
                { "code": "TransferFinalized", "retryable": false }
                """),
            _ => throw new InvalidOperationException($"Unexpected API route {request.RequestUri}.")
        });
        var storage = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var apiHttp = CreateHttpClient(api, "https://steward.test/");
        using var storageHttp = new HttpClient(storage);
        var client = new StewardPackageUploadClient(apiHttp, storageHttp);
        await using var package = new MemoryStream(bytes, writable: false);

        var result = await client.UploadAsync(
            worldId,
            revisionId,
            RemotePackageKind.State,
            package,
            environmentId,
            "access-token");

        Assert.Equal(RemotePackageUploadStatus.Published, result.Status);
        Assert.Equal(revisionId, result.RevisionId);
        Assert.Equal(bytes.Length, result.ByteSize);
        Assert.Equal(sha256, result.Sha256);
        Assert.Equal(transferId, result.TransferId);

        Assert.Equal(5, api.Requests.Count);
        Assert.All(api.Requests, request => Assert.Equal("Bearer access-token", request.Authorization));
        var begin = api.Requests[0];
        using (var body = JsonDocument.Parse(begin.Body!))
        {
            Assert.Equal(bytes.Length, body.RootElement.GetProperty("expectedByteSize").GetInt64());
            Assert.Equal(sha256, body.RootElement.GetProperty("expectedSha256").GetString());
            Assert.Equal(environmentId.Value, body.RootElement.GetProperty("requiredEnvironmentRevisionId").GetGuid());
        }

        Assert.Equal(2, storage.Requests.Count);
        Assert.Equal("https://storage.test/part-2", storage.Requests[0].Uri);
        Assert.Equal("efgh", Encoding.ASCII.GetString(storage.Requests[0].BodyBytes!));
        Assert.Equal("two", storage.Requests[0].Headers["x-steward-test"]);
        Assert.Null(storage.Requests[0].Authorization);
        Assert.Equal("https://storage.test/part-3", storage.Requests[1].Uri);
        Assert.Equal("ij", Encoding.ASCII.GetString(storage.Requests[1].BodyBytes!));
        Assert.Equal("three", storage.Requests[1].Headers["x-steward-test"]);
        Assert.Null(storage.Requests[1].Authorization);
    }

    [Fact]
    public async Task AlreadyPublishedDoesNotTouchObjectStorage()
    {
        var bytes = Encoding.ASCII.GetBytes("already-there");
        var api = new RecordingHandler(_ => JsonResponse(
            HttpStatusCode.OK,
            """
            { "code": "AlreadyPublished", "retryable": false }
            """));
        var storage = new RecordingHandler(_ => throw new InvalidOperationException("Object storage must not be used."));
        using var apiHttp = CreateHttpClient(api, "https://steward.test/");
        using var storageHttp = new HttpClient(storage);
        var client = new StewardPackageUploadClient(apiHttp, storageHttp);
        await using var package = new MemoryStream(bytes, writable: false);

        var result = await client.UploadAsync(
            WorldId.New(),
            RevisionId.New(),
            RemotePackageKind.State,
            package,
            requiredEnvironmentRevisionId: null,
            "access-token");

        Assert.Equal(RemotePackageUploadStatus.AlreadyPublished, result.Status);
        Assert.Single(api.Requests);
        Assert.Empty(storage.Requests);
    }

    [Fact]
    public async Task CorruptCompletedPartProgressFailsClosedBeforeObjectStorageWrite()
    {
        var worldId = WorldId.New();
        var revisionId = RevisionId.New();
        var transferId = Guid.NewGuid();
        var bytes = Encoding.ASCII.GetBytes("abcdefgh");
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        var api = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path == $"/api/v1/worlds/{worldId.Value:D}/transfers" => JsonResponse(
                HttpStatusCode.Created,
                TransferStartedJson(transferId, worldId, revisionId, null, bytes.Length, sha256, 4, 2)),
            var path when path == $"/api/v1/transfers/{transferId:D}" => JsonResponse(
                HttpStatusCode.OK,
                ProgressJson(transferId, worldId, revisionId, null, bytes.Length, sha256, 4, 2,
                    completedPartNumber: 1,
                    completedPartBytes: 3)),
            _ => throw new InvalidOperationException($"Unexpected API route {request.RequestUri}.")
        });
        var storage = new RecordingHandler(_ => throw new InvalidOperationException("Object storage must not be used."));
        using var apiHttp = CreateHttpClient(api, "https://steward.test/");
        using var storageHttp = new HttpClient(storage);
        var client = new StewardPackageUploadClient(apiHttp, storageHttp);
        await using var package = new MemoryStream(bytes, writable: false);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.UploadAsync(
            worldId,
            revisionId,
            RemotePackageKind.State,
            package,
            requiredEnvironmentRevisionId: null,
            "access-token"));

        Assert.Contains("part 1", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(storage.Requests);
    }

    [Fact]
    public async Task ProviderCompletedProgressSkipsPartWritesAndFinalizes()
    {
        var worldId = WorldId.New();
        var revisionId = RevisionId.New();
        var transferId = Guid.NewGuid();
        var bytes = Encoding.ASCII.GetBytes("abcdefgh");
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        var api = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path == $"/api/v1/worlds/{worldId.Value:D}/transfers" => JsonResponse(
                HttpStatusCode.Created,
                TransferStartedJson(transferId, worldId, revisionId, null, bytes.Length, sha256, 4, 2)),
            var path when path == $"/api/v1/transfers/{transferId:D}" => JsonResponse(
                HttpStatusCode.OK,
                ProgressJson(transferId, worldId, revisionId, null, bytes.Length, sha256, 4, 2,
                    completedPartNumber: null,
                    completedPartBytes: null,
                    providerUploadCompleted: true)),
            var path when path == $"/api/v1/transfers/{transferId:D}/finalize" => JsonResponse(
                HttpStatusCode.OK,
                """
                { "code": "AlreadyFinalized", "retryable": false }
                """),
            _ => throw new InvalidOperationException($"Unexpected API route {request.RequestUri}.")
        });
        var storage = new RecordingHandler(_ => throw new InvalidOperationException("Object storage must not be used."));
        using var apiHttp = CreateHttpClient(api, "https://steward.test/");
        using var storageHttp = new HttpClient(storage);
        var client = new StewardPackageUploadClient(apiHttp, storageHttp);
        await using var package = new MemoryStream(bytes, writable: false);

        var result = await client.UploadAsync(
            worldId,
            revisionId,
            RemotePackageKind.State,
            package,
            requiredEnvironmentRevisionId: null,
            "access-token");

        Assert.Equal(RemotePackageUploadStatus.Published, result.Status);
        Assert.Empty(storage.Requests);
        Assert.Equal(3, api.Requests.Count);
    }

    [Fact]
    public async Task ObjectStorageFailureLeavesTransferResumable()
    {
        var worldId = WorldId.New();
        var revisionId = RevisionId.New();
        var transferId = Guid.NewGuid();
        var bytes = Encoding.ASCII.GetBytes("abcd");
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        var api = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var path when path == $"/api/v1/worlds/{worldId.Value:D}/transfers" => JsonResponse(
                HttpStatusCode.Created,
                TransferStartedJson(transferId, worldId, revisionId, null, bytes.Length, sha256, 4, 1)),
            var path when path == $"/api/v1/transfers/{transferId:D}" => JsonResponse(
                HttpStatusCode.OK,
                ProgressJson(transferId, worldId, revisionId, null, bytes.Length, sha256, 4, 1,
                    completedPartNumber: null,
                    completedPartBytes: null)),
            var path when path == $"/api/v1/transfers/{transferId:D}/parts/1/authorization" => JsonResponse(
                HttpStatusCode.OK,
                PartAuthorizationJson("https://storage.test/part-1", 4, "one")),
            _ => throw new InvalidOperationException($"Unexpected API route {request.RequestUri}.")
        });
        var storage = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var apiHttp = CreateHttpClient(api, "https://steward.test/");
        using var storageHttp = new HttpClient(storage);
        var client = new StewardPackageUploadClient(apiHttp, storageHttp);
        await using var package = new MemoryStream(bytes, writable: false);

        var exception = await Assert.ThrowsAsync<IOException>(() => client.UploadAsync(
            worldId,
            revisionId,
            RemotePackageKind.State,
            package,
            requiredEnvironmentRevisionId: null,
            "access-token"));

        Assert.Contains("503", exception.Message, StringComparison.Ordinal);
        Assert.Equal(3, api.Requests.Count);
        Assert.Single(storage.Requests);
        Assert.DoesNotContain(api.Requests, request => request.Uri!.EndsWith("/finalize", StringComparison.Ordinal));
    }

    private static string TransferStartedJson(
        Guid transferId,
        WorldId worldId,
        RevisionId revisionId,
        RevisionId? environmentId,
        long bytes,
        string sha256,
        int partSize,
        int partCount)
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
            "requiredEnvironmentRevisionId": {{NullableGuidJson(environmentId)}},
            "partSizeBytes": {{partSize}},
            "partCount": {{partCount}},
            "expiresAt": "2026-07-23T12:00:00Z",
            "state": "Active"
          },
          "retryable": false
        }
        """;

    private static string ProgressJson(
        Guid transferId,
        WorldId worldId,
        RevisionId revisionId,
        RevisionId? environmentId,
        long bytes,
        string sha256,
        int partSize,
        int partCount,
        int? completedPartNumber,
        long? completedPartBytes,
        bool providerUploadCompleted = false)
    {
        var parts = completedPartNumber is { } number && completedPartBytes is { } partBytes
            ? $$"[{ "partNumber": {{number}}, "byteSize": {{partBytes}} }]"
            : "[]";
        return $$"""
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
              "requiredEnvironmentRevisionId": {{NullableGuidJson(environmentId)}},
              "partSizeBytes": {{partSize}},
              "partCount": {{partCount}},
              "expiresAt": "2026-07-23T12:00:00Z",
              "state": "Active"
            },
            "completedParts": {{parts}},
            "providerUploadCompleted": {{providerUploadCompleted.ToString().ToLowerInvariant()}}
          },
          "retryable": false
        }
        """;
    }

    private static string PartAuthorizationJson(string uri, long bytes, string headerValue)
        => $$"""
        {
          "code": "PartAuthorized",
          "data": {
            "uri": "{{uri}}",
            "method": "PUT",
            "requiredHeaders": { "x-steward-test": "{{headerValue}}" },
            "expiresAt": "2026-07-23T12:00:00Z",
            "expectedByteSize": {{bytes}}
          },
          "retryable": false
        }
        """;

    private static string NullableGuidJson(RevisionId? revisionId)
        => revisionId is { } value ? $"\"{value.Value:D}\"" : "null";

    private static HttpClient CreateHttpClient(HttpMessageHandler handler, string baseAddress)
        => new(handler)
        {
            BaseAddress = new Uri(baseAddress)
        };

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var bodyBytes = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var headers = request.Headers
                .Concat(request.Content?.Headers ?? [])
                .ToDictionary(
                    header => header.Key,
                    header => string.Join(",", header.Value),
                    StringComparer.OrdinalIgnoreCase);
            Requests.Add(new RequestSnapshot(
                request.Method,
                request.RequestUri?.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                bodyBytes,
                headers));
            return _responseFactory(request);
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        string? Uri,
        string? Authorization,
        byte[]? BodyBytes,
        IReadOnlyDictionary<string, string> Headers)
    {
        public string? Body => BodyBytes is null ? null : Encoding.UTF8.GetString(BodyBytes);
    }
}
