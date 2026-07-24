using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using SharedWorlds.Backend.ObjectStorage.S3;
using SharedWorlds.Backend.Transfers;
using Xunit;

namespace SharedWorlds.Backend.ObjectStorage.S3.Tests;

public sealed class S3CompatibleLargeTransferStressTests
{
    private const int MiB = 1024 * 1024;
    private const int PartSizeBytes = 64 * MiB;
    private const int PartCount = 4;
    private const long TotalBytes = (long)PartSizeBytes * PartCount;

    [Fact]
    public async Task TwoHundredFiftySixMiBUploadResumesAfterPartialRestartAndStreamsBackWithExactHash()
    {
        var settings = TestSettings.Load();
        var bucket = $"steward-stress-{Guid.NewGuid():N}";
        var objectKey = $"packages/stress/{Guid.NewGuid():N}.package";
        var expectedSha256 = await ComputePatternSha256Async(TotalBytes);
        var protocol = GetProtocol(settings.Endpoint);

        using var clientOne = CreateClient(settings);
        await clientOne.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        try
        {
            string durableHandle;
            using (var firstStore = new S3CompatibleImmutableObjectStore(clientOne, bucket, protocol))
            {
                var upload = await firstStore.BeginMultipartUploadAsync(
                    objectKey,
                    TotalBytes,
                    expectedSha256);
                durableHandle = upload.ProviderUploadId;

                using var http = new HttpClient();
                await UploadPartAsync(firstStore, http, durableHandle, partNumber: 1);
                await UploadPartAsync(firstStore, http, durableHandle, partNumber: 2);

                var partial = Assert.IsType<ImmutableUploadSnapshot>(
                    await firstStore.GetMultipartUploadAsync(durableHandle));
                Assert.Equal(2, partial.CompletedParts.Count);
                Assert.False(partial.IsCompleted);
            }

            using var clientTwo = CreateClient(settings);
            using var restartedStore = new S3CompatibleImmutableObjectStore(clientTwo, bucket, protocol);
            var resumed = Assert.IsType<ImmutableUploadSnapshot>(
                await restartedStore.GetMultipartUploadAsync(durableHandle));
            Assert.Equal(2, resumed.CompletedParts.Count);

            using (var http = new HttpClient())
            {
                await UploadPartAsync(restartedStore, http, durableHandle, partNumber: 3);
                await UploadPartAsync(restartedStore, http, durableHandle, partNumber: 4);
            }

            var stored = await restartedStore.CompleteMultipartUploadAsync(durableHandle);
            Assert.Equal(objectKey, stored.ObjectKey);
            Assert.Equal(TotalBytes, stored.ByteSize);
            Assert.Equal(expectedSha256, stored.Sha256);

            var download = await restartedStore.AuthorizeDownloadAsync(
                objectKey,
                TotalBytes,
                DateTimeOffset.UtcNow.AddMinutes(10));
            Assert.Equal("GET", download.Method);

            using var downloadHttp = new HttpClient();
            using var response = await downloadHttp.GetAsync(
                download.Uri,
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var downloadStream = await response.Content.ReadAsStreamAsync();
            var downloaded = await HashAndCountAsync(downloadStream);
            Assert.Equal(TotalBytes, downloaded.ByteCount);
            Assert.Equal(expectedSha256, downloaded.Sha256);

            await restartedStore.DeleteObjectAsync(objectKey);
            Assert.Null(await restartedStore.InspectObjectAsync(objectKey));
        }
        finally
        {
            await AbortMultipartUploadsAsync(clientOne, bucket);
            await DeleteBucketContentsAsync(clientOne, bucket);
            await clientOne.DeleteBucketAsync(bucket);
        }
    }

    private static async Task UploadPartAsync(
        S3CompatibleImmutableObjectStore store,
        HttpClient http,
        string durableHandle,
        int partNumber)
    {
        var authorization = await store.AuthorizeUploadPartAsync(
            durableHandle,
            partNumber,
            PartSizeBytes,
            DateTimeOffset.UtcNow.AddMinutes(10));
        Assert.Equal("PUT", authorization.Method);
        Assert.Equal(PartSizeBytes, authorization.ExpectedByteSize);

        var absoluteOffset = (long)(partNumber - 1) * PartSizeBytes;
        using var source = new RepeatingPatternStream(absoluteOffset, PartSizeBytes);
        using var content = new StreamContent(source, MiB);
        content.Headers.ContentLength = PartSizeBytes;
        using var response = await http.PutAsync(authorization.Uri, content);
        Assert.True(
            response.IsSuccessStatusCode,
            $"Presigned stress part {partNumber} failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).");
    }

    private static async Task<string> ComputePatternSha256Async(long byteCount)
    {
        using var source = new RepeatingPatternStream(0, byteCount);
        var hashed = await HashAndCountAsync(source);
        Assert.Equal(byteCount, hashed.ByteCount);
        return hashed.Sha256;
    }

    private static async Task<(long ByteCount, string Sha256)> HashAndCountAsync(Stream stream)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[MiB];
        long byteCount = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            byteCount += read;
        }

        return (byteCount, Convert.ToHexString(hash.GetHashAndReset()));
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

    private static Protocol GetProtocol(Uri endpoint)
        => endpoint.Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS;

    private static async Task AbortMultipartUploadsAsync(IAmazonS3 client, string bucket)
    {
        var response = await client.ListMultipartUploadsAsync(
            new ListMultipartUploadsRequest { BucketName = bucket });
        foreach (var upload in response.MultipartUploads ?? [])
        {
            if (string.IsNullOrWhiteSpace(upload.Key) || string.IsNullOrWhiteSpace(upload.UploadId))
            {
                continue;
            }

            await client.AbortMultipartUploadAsync(
                new AbortMultipartUploadRequest
                {
                    BucketName = bucket,
                    Key = upload.Key,
                    UploadId = upload.UploadId
                });
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

    private sealed class RepeatingPatternStream : Stream
    {
        private static readonly byte[] Pattern = CreatePattern();
        private readonly long _absoluteOffset;
        private readonly long _length;
        private long _position;

        public RepeatingPatternStream(long absoluteOffset, long length)
        {
            if (absoluteOffset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(absoluteOffset));
            }

            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            _absoluteOffset = absoluteOffset;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= _length || buffer.IsEmpty)
            {
                return 0;
            }

            var read = (int)Math.Min(buffer.Length, _length - _position);
            Fill(buffer[..read], _absoluteOffset + _position);
            _position += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static void Fill(Span<byte> destination, long absoluteOffset)
        {
            var patternOffset = (int)(absoluteOffset % Pattern.Length);
            while (!destination.IsEmpty)
            {
                var copy = Math.Min(Pattern.Length - patternOffset, destination.Length);
                Pattern.AsSpan(patternOffset, copy).CopyTo(destination);
                destination = destination[copy..];
                patternOffset = 0;
            }
        }

        private static byte[] CreatePattern()
        {
            var pattern = new byte[251];
            for (var index = 0; index < pattern.Length; index++)
            {
                pattern[index] = (byte)((index * 31 + 17) % 251);
            }

            return pattern;
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
