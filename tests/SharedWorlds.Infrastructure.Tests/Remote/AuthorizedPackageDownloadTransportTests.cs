using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class AuthorizedPackageDownloadTransportTests
{
    private const string Sha256 = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Theory]
    [InlineData("https://objects.example/package")]
    [InlineData("http://localhost:9000/package")]
    [InlineData("http://127.0.0.1:9000/package")]
    [InlineData("http://[::1]:9000/package")]
    public void SecureRemoteAndLoopbackUrisAreAccepted(string uri)
    {
        var authorization = new AuthorizedPackageDownload(
            new Uri(uri),
            new Dictionary<string, string>(),
            DateTimeOffset.UtcNow.AddMinutes(5),
            expectedByteSize: 1,
            Sha256);

        Assert.Equal(uri, authorization.Uri.AbsoluteUri.TrimEnd('/'));
    }

    [Theory]
    [InlineData("http://objects.example/package")]
    [InlineData("http://10.1.2.3:9000/package")]
    [InlineData("http://192.168.1.20:9000/package")]
    public void PlaintextRemoteUrisAreRejected(string uri)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new AuthorizedPackageDownload(
                new Uri(uri),
                new Dictionary<string, string>(),
                DateTimeOffset.UtcNow.AddMinutes(5),
                expectedByteSize: 1,
                Sha256));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
    }
}
