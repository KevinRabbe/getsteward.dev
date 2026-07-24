using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardRemoteEndpointPolicyTests
{
    [Theory]
    [InlineData("https://steward.example", "https://steward.example/")]
    [InlineData("https://steward.example/api", "https://steward.example/api/")]
    [InlineData("http://localhost:18080", "http://localhost:18080/")]
    [InlineData("http://127.0.0.1:18080", "http://127.0.0.1:18080/")]
    [InlineData("http://[::1]:18080", "http://[::1]:18080/")]
    public void HttpsAndLoopbackHttpAreAcceptedAndNormalized(
        string input,
        string expected)
    {
        var accepted = StewardRemoteEndpointPolicy.TryNormalizeApiBaseAddress(
            new Uri(input),
            out var normalized);

        Assert.True(accepted);
        Assert.NotNull(normalized);
        Assert.Equal(expected, normalized.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://steward.example")]
    [InlineData("http://10.20.30.40:18080")]
    [InlineData("http://192.168.1.25:18080")]
    [InlineData("ftp://steward.example")]
    public void PlaintextRemoteAndNonHttpEndpointsAreRejected(string input)
    {
        var accepted = StewardRemoteEndpointPolicy.TryNormalizeApiBaseAddress(
            new Uri(input),
            out var normalized);

        Assert.False(accepted);
        Assert.Null(normalized);
    }

    [Fact]
    public void NormalizeFailsClosedForPlaintextRemoteEndpoint()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            StewardRemoteEndpointPolicy.NormalizeApiBaseAddress(
                new Uri("http://steward.example")));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
        Assert.Contains("loopback", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RelativeEndpointIsRejected()
    {
        var accepted = StewardRemoteEndpointPolicy.TryNormalizeApiBaseAddress(
            new Uri("api/v1", UriKind.Relative),
            out var normalized);

        Assert.False(accepted);
        Assert.Null(normalized);
    }
}
