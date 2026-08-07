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
    [InlineData("http://172.30.0.40:9000")]
    [InlineData("http://192.168.1.50:9000")]
    public void PlaintextRemoteServiceUrlsAreRejectedByDefault(string serviceUrl)
    {
        var exception = Assert.Throws<ArgumentException>(() => Create(serviceUrl));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://10.2.3.4:9000")]
    [InlineData("http://172.16.0.1:9000")]
    [InlineData("http://172.30.0.40:9000")]
    [InlineData("http://172.31.255.254:9000")]
    [InlineData("http://192.168.1.50:9000")]
    public void ExplicitPrivateNetworkPlaintextAcceptsOnlyRfc1918Ipv4Literals(string serviceUrl)
    {
        var options = Create(serviceUrl, allowInsecurePrivateNetwork: true);

        Assert.Equal(new Uri(serviceUrl), options.ServiceUrl);
        Assert.True(options.AllowInsecurePrivateNetwork);
    }

    [Theory]
    [InlineData("http://8.8.8.8:9000")]
    [InlineData("http://172.15.255.255:9000")]
    [InlineData("http://172.32.0.1:9000")]
    [InlineData("http://169.254.1.1:9000")]
    [InlineData("http://minio.internal:9000")]
    [InlineData("http://s3.example:9000")]
    public void ExplicitPrivateNetworkPlaintextStillRejectsNonRfc1918Targets(string serviceUrl)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            Create(serviceUrl, allowInsecurePrivateNetwork: true));

        Assert.Contains("RFC1918 IPv4 literal", exception.Message, StringComparison.Ordinal);
    }

    private static S3CompatibleObjectStoreOptions Create(
        string serviceUrl,
        bool allowInsecurePrivateNetwork = false)
        => new(
            new Uri(serviceUrl),
            authenticationRegion: "eu-test-1",
            bucketName: "steward-test",
            accessKeyId: "access-key",
            secretAccessKey: "secret-key",
            forcePathStyle: true,
            allowInsecurePrivateNetwork: allowInsecurePrivateNetwork);
}
