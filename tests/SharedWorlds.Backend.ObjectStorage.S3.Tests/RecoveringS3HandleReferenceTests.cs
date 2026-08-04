using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using SharedWorlds.Backend.ObjectStorage.S3;
using SharedWorlds.Backend.Transfers;
using Xunit;

namespace SharedWorlds.Backend.ObjectStorage.S3.Tests;

public sealed class RecoveringS3HandleReferenceTests
{
    private const int MiB = 1024 * 1024;
    private const string ReferenceObjectPrefix = "steward-system/private-upload-handles/";

    [Fact]
    public async Task BoundedReferenceSurvivesRestartAndCompletionReplayUntilObjectDeletion()
    {
        var settings = TestSettings.Load();
        var bucket = $"steward-reference-{Guid.NewGuid():N}";
        var objectKey = $"private-snapshots/test/{Guid.NewGuid():N}.package";
        var bytes = CreatePayload(6 * MiB);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        var protocol = GetProtocol(settings.Endpoint);

        using var clientOne = CreateClient(settings);
        await clientOne.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        try
        {
            string reference;
            using (var firstStore = new RecoveringS3CompatibleImmutableObjectStore(
                       clientOne,
                       bucket,
                       protocol))
            {
                var upload = await firstStore.BeginMultipartUploadAsync(
                    objectKey,
                    bytes.LongLength,
                    sha256);
                reference = upload.ProviderUploadId;
                Assert.StartsWith("s3ref1_", reference, StringComparison.Ordinal);
                Assert.True(reference.Length <= 512);
                Assert.Single(await ListReferenceObjectsAsync(clientOne, bucket));

                var part = await firstStore.AuthorizeUploadPartAsync(
                    reference,
                    partNumber: 1,
                    expectedByteSize: bytes.LongLength,
                    expiresAt: DateTimeOffset.UtcNow.AddMinutes(10));
                using var http = new HttpClient();
                await PutPartAsync(http, part, bytes);
            }

            using var clientTwo = CreateClient(settings);
            using var restartedStore = new RecoveringS3CompatibleImmutableObjectStore(
                clientTwo,
                bucket,
                protocol);
            var resumed = Assert.IsType<ImmutableUploadSnapshot>(
                await restartedStore.GetMultipartUploadAsync(reference));
            Assert.Equal(objectKey, resumed.ObjectKey);
            Assert.Single(resumed.CompletedParts);

            var stored = await restartedStore.CompleteMultipartUploadAsync(reference);
            var replay = await restartedStore.CompleteMultipartUploadAsync(reference);
            Assert.Equal(stored, replay);
            Assert.Equal(bytes.LongLength, stored.ByteSize);
            Assert.Equal(sha256, stored.Sha256);
            Assert.Single(await ListReferenceObjectsAsync(clientTwo, bucket));

            await restartedStore.DeleteObjectAsync(objectKey);
            Assert.Null(await restartedStore.InspectObjectAsync(objectKey));
            Assert.Empty(await ListReferenceObjectsAsync(clientTwo, bucket));
        }
        finally
        {
            await AbortMultipartUploadsAsync(clientOne, bucket);
            await DeleteBucketContentsAsync(clientOne, bucket);
            await clientOne.DeleteBucketAsync(bucket);
        }
    }

    [Fact]
    public async Task LegacyHandleRemainsReadableAndTamperedReferenceFailsClosed()
    {
        var settings = TestSettings.Load();
        var bucket = $"steward-reference-{Guid.NewGuid():N}";
        var protocol = GetProtocol(settings.Endpoint);
        using var client = CreateClient(settings);
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        try
        {
            var legacyObjectKey = $"private-snapshots/test/{Guid.NewGuid():N}.package";
            var legacyBytes = CreatePayload(6 * MiB);
            var legacySha256 = Convert.ToHexString(SHA256.HashData(legacyBytes));
            string legacyHandle;
            using (var legacyStore = new S3CompatibleImmutableObjectStore(
                       client,
                       bucket,
                       protocol))
            {
                legacyHandle = (await legacyStore.BeginMultipartUploadAsync(
                    legacyObjectKey,
                    legacyBytes.LongLength,
                    legacySha256)).ProviderUploadId;
            }

            using var recoveringStore = new RecoveringS3CompatibleImmutableObjectStore(
                client,
                bucket,
                protocol);
            Assert.StartsWith("s3mp1_", legacyHandle, StringComparison.Ordinal);
            var legacyProgress = Assert.IsType<ImmutableUploadSnapshot>(
                await recoveringStore.GetMultipartUploadAsync(legacyHandle));
            Assert.Equal(legacyObjectKey, legacyProgress.ObjectKey);
            await recoveringStore.AbortMultipartUploadAsync(legacyHandle);

            var referencedObjectKey = $"private-snapshots/test/{Guid.NewGuid():N}.package";
            var referencedBytes = CreatePayload(6 * MiB);
            var referencedSha256 = Convert.ToHexString(SHA256.HashData(referencedBytes));
            var reference = (await recoveringStore.BeginMultipartUploadAsync(
                referencedObjectKey,
                referencedBytes.LongLength,
                referencedSha256)).ProviderUploadId;
            var referenceObject = Assert.Single(
                await ListReferenceObjectsAsync(client, bucket));
            await using (var tampered = new MemoryStream(
                             Encoding.UTF8.GetBytes("tampered-provider-handle"),
                             writable: false))
            {
                await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = referenceObject.Key,
                    InputStream = tampered,
                    AutoCloseStream = false,
                    ContentType = "application/octet-stream"
                });
            }

            await Assert.ThrowsAsync<InvalidDataException>(
                () => recoveringStore.GetMultipartUploadAsync(reference));
        }
        finally
        {
            await AbortMultipartUploadsAsync(client, bucket);
            await DeleteBucketContentsAsync(client, bucket);
            await client.DeleteBucketAsync(bucket);
        }
    }

    private static AmazonS3Client CreateClient(TestSettings settings)
        => new(
            new BasicAWSCredentials(settings.AccessKey, settings.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = settings.Endpoint.AbsoluteUri.TrimEnd('/'),
                AuthenticationRegion = settings.Region,
                ForcePathStyle = true
            });

    private static Protocol GetProtocol(Uri endpoint)
        => endpoint.Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS;

    private static async Task PutPartAsync(
        HttpClient http,
        DirectObjectTransferAuthorization authorization,
        byte[] bytes)
    {
        Assert.Equal("PUT", authorization.Method);
        Assert.Equal(bytes.LongLength, authorization.ExpectedByteSize);
        using var response = await http.PutAsync(
            authorization.Uri,
            new ByteArrayContent(bytes));
        Assert.True(
            response.IsSuccessStatusCode,
            $"Presigned part upload failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).");
    }

    private static async Task<IReadOnlyList<S3Object>> ListReferenceObjectsAsync(
        IAmazonS3 client,
        string bucket)
    {
        var response = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket,
            Prefix = ReferenceObjectPrefix
        });
        return response.S3Objects ?? [];
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

    private static async Task AbortMultipartUploadsAsync(IAmazonS3 client, string bucket)
    {
        var response = await client.ListMultipartUploadsAsync(
            new ListMultipartUploadsRequest { BucketName = bucket });
        foreach (var upload in response.MultipartUploads ?? [])
        {
            if (!string.IsNullOrWhiteSpace(upload.Key) &&
                !string.IsNullOrWhiteSpace(upload.UploadId))
            {
                await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = bucket,
                    Key = upload.Key,
                    UploadId = upload.UploadId
                });
            }
        }
    }

    private static async Task DeleteBucketContentsAsync(IAmazonS3 client, string bucket)
    {
        var response = await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket
        });
        foreach (var entry in response.S3Objects ?? [])
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
            => new(
                new Uri(Required("STEWARD_TEST_S3_ENDPOINT"), UriKind.Absolute),
                Required("STEWARD_TEST_S3_REGION"),
                Required("STEWARD_TEST_S3_ACCESS_KEY"),
                Required("STEWARD_TEST_S3_SECRET_KEY"));

        private static string Required(string name)
            => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException(
                    $"{name} must be set for S3-compatible integration tests.");
    }
}
