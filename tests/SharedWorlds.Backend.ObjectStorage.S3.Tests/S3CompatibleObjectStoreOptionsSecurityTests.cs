using SharedWorlds.Backend.ObjectStorage.S3;
using Xunit;

namespace SharedWorlds.Backend.ObjectStorage.S3.Tests;

public sealed class S3CompatibleObjectStoreOptionsSecurityTests
{
    [Theory]
    [InlineData("https://s3.example")]
    [InlineData("http://localhost:9000")]
    [InlineData("http://127.0.0.1:9000")]
    [InlineData("http://[::1]:9000")]
    public void SecureRemoteAndLoopbackServiceUrlsAreAccepted(string serviceUrl)
    {
        var options = Create(serviceUrl);

        Assert.Equal(new Uri(serviceUrl), options.ServiceUrl);
    }

    [Theory]
    [InlineData("http://s3.example")]
    [InlineData("http://10.2.3.4:9000")]
    [InlineData("http://192.168.1.50:9000")]
    public void PlaintextRemoteServiceUrlsAreRejected(string serviceUrl)
    {
        var exception = Assert.Throws<ArgumentException>(() => Create(serviceUrl));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    private static S3CompatibleObjectStoreOptions Create(string serviceUrl)
        => new(
            new Uri(serviceUrl),
            authenticationRegion: "eu-test-1",
            bucketName: "steward-test",
            accessKeyId: "access-key",
            secretAccessKey: "secret-key",
            forcePathStyle: true);
}
