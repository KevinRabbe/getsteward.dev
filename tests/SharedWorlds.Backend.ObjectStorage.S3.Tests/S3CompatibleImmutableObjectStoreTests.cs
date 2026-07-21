using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using SharedWorlds.Backend.ObjectStorage.S3;
using SharedWorlds.Backend.Transfers;
using Xunit;

namespace SharedWorlds.Backend.ObjectStorage.S3.Tests;

public sealed class S3CompatibleImmutableObjectStoreTests
{
    private const int MiB = 1024 * 1024;

    [Fact]
    public async Task MultipartUploadSurvivesAdapterRestartAndCompletesThroughPresignedParts()
    {
        var settings = TestSettings.Load();
        var bucket = $"steward-test-{Guid.NewGuid():N}";
        var objectKey = $"packages/test/{Guid.NewGuid():N}.package";
        var bytes = CreatePayload(7 * MiB);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));

        using var clientOne = CreateClient(settings);
        await clientOne.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        try
        {
            string durableHandle;
            using (var firstStore = new S3CompatibleImmutableObjectStore(clientOne, bucket))
            {
                var upload = await firstStore.BeginMultipartUploadAsync(
                    objectKey,
                    bytes.LongLength,
                    sha256);
                durableHandle = upload.ProviderUploadId;

                var firstPart = await firstStore.AuthorizeUploadPartAsync(
                    durableHandle,
                    partNumber: 1,
                    expectedByteSize: 6L * MiB,
                    expiresAt: DateTimeOffset.UtcNow.AddMinutes(10));
                var secondPart = await firstStore.AuthorizeUploadPartAsync(
                    durableHandle,
                    partNumber: 2,
                    expectedByteSize: 1L * MiB,
                    expiresAt: DateTimeOffset.UtcNow.AddMinutes(10));

                using var http = new HttpClient();
                await PutPartAsync(http, firstPart, bytes.AsMemory(0, 6 * MiB));
                await PutPartAsync(http, secondPart, bytes.AsMemory(6 * MiB, 1 * MiB));

                var progress = Assert.IsType<ImmutableUploadSnapshot>(
                    await firstStore.GetMultipartUploadAsync(durableHandle));
                Assert.Equal(objectKey, progress.ObjectKey);
                Assert.Equal(2, progress.CompletedParts.Count);
                Assert.False(progress.IsCompleted);
            }

            using var clientTwo = CreateClient(settings);
            using var restartedStore = new S3CompatibleImmutableObjectStore(clientTwo, bucket);

            var resumed = Assert.IsType<ImmutableUploadSnapshot>(
                await restartedStore.GetMultipartUploadAsync(durableHandle));
            Assert.Equal(2, resumed.CompletedParts.Count);

            var stored = await restartedStore.CompleteMultipartUploadAsync(durableHandle);
            Assert.Equal(objectKey, stored.ObjectKey);
            Assert.Equal(bytes.LongLength, stored.ByteSize);
            Assert.Equal(sha256, stored.Sha256);

            var replay = await restartedStore.CompleteMultipartUploadAsync(durableHandle);
            Assert.Equal(stored, replay);

            var download = await restartedStore.AuthorizeDownloadAsync(
                objectKey,
                bytes.LongLength,
                DateTimeOffset.UtcNow.AddMinutes(10));
            Assert.Equal("GET", download.Method);

            using var downloadHttp = new HttpClient();
            var downloaded = await downloadHttp.GetByteArrayAsync(download.Uri);
            Assert.Equal(bytes, downloaded);

            var inspected = Assert.IsType<ImmutableStoredObject>(
                await restartedStore.InspectObjectAsync(objectKey));
            Assert.Equal(stored, inspected);

            await restartedStore.DeleteObjectAsync(objectKey);
            Assert.Null(await restartedStore.InspectObjectAsync(objectKey));
        }
        finally
        {
            await DeleteBucketContentsAsync(clientOne, bucket);
            await clientOne.DeleteBucketAsync(bucket);
        }
    }

    [Fact]
    public async Task AbortIsIdempotentAndDoesNotCreateAnObject()
    {
        var settings = TestSettings.Load();
        var bucket = $"steward-test-{Guid.NewGuid():N}";
        var objectKey = $"packages/test/{Guid.NewGuid():N}.package";
        var bytes = CreatePayload(6 * MiB);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));

        using var client = CreateClient(settings);
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        try
        {
            using var store = new S3CompatibleImmutableObjectStore(client, bucket);
            var upload = await store.BeginMultipartUploadAsync(
                objectKey,
                bytes.LongLength,
                sha256);

            await store.AbortMultipartUploadAsync(upload.ProviderUploadId);
            await store.AbortMultipartUploadAsync(upload.ProviderUploadId);

            Assert.Null(await store.GetMultipartUploadAsync(upload.ProviderUploadId));
            Assert.Null(await store.InspectObjectAsync(objectKey));
        }
        finally
        {
            await DeleteBucketContentsAsync(client, bucket);
            await client.DeleteBucketAsync(bucket);
        }
    }

    private static AmazonS3Client CreateClient(TestSettings settings)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = settings.Endpoint.AbsoluteUri.TrimEnd('/'),
            AuthenticationRegion = settings.Region,
            ForcePathStyle = true
        };
        return new AmazonS3Client(
            new BasicAWSCredentials(settings.AccessKey, settings.SecretKey),
            config);
    }

    private static async Task PutPartAsync(
        HttpClient http,
        DirectObjectTransferAuthorization authorization,
        ReadOnlyMemory<byte> bytes)
    {
        Assert.Equal("PUT", authorization.Method);
        Assert.Equal(bytes.Length, authorization.ExpectedByteSize);

        using var content = new ByteArrayContent(bytes.ToArray());
        using var response = await http.PutAsync(authorization.Uri, content);
        Assert.True(
            response.IsSuccessStatusCode,
            $"Presigned part upload failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).");
    }

    private static byte[] CreatePayload(int byteCount)
    {
        var bytes = new byte[byteCount];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)((index * 31 + 17) % 251);
        }

        return bytes;
    }

    private static async Task DeleteBucketContentsAsync(IAmazonS3 client, string bucket)
    {
        var response = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket
        });
        foreach (var entry in response.S3Objects)
        {
            await client.DeleteObjectAsync(bucket, entry.Key);
        }
    }

    private sealed record TestSettings(
        Uri Endpoint,
        string Region,
        string AccessKey,
        string SecretKey)
    {
        public static TestSettings Load()
        {
            var endpoint = Required("STEWARD_TEST_S3_ENDPOINT");
            var region = Required("STEWARD_TEST_S3_REGION");
            var accessKey = Required("STEWARD_TEST_S3_ACCESS_KEY");
            var secretKey = Required("STEWARD_TEST_S3_SECRET_KEY");
            return new TestSettings(new Uri(endpoint, UriKind.Absolute), region, accessKey, secretKey);
        }

        private static string Required(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"{name} must be set for S3-compatible integration tests.");
            }

            return value;
        }
    }
}
