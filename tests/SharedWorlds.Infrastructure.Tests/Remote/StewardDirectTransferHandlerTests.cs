using System.Net;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardDirectTransferHandlerTests
{
    [Theory]
    [InlineData("https://objects.example/package")]
    [InlineData("http://localhost:9000/package")]
    [InlineData("http://127.0.0.1:9000/package")]
    [InlineData("http://[::1]:9000/package")]
    public async Task SecureRemoteAndLoopbackRequestsReachInnerHandler(string uri)
    {
        var inner = new RecordingHandler();
        using var client = new HttpClient(new StewardDirectTransferHandler(inner));

        using var response = await client.GetAsync(uri);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, inner.Calls);
    }

    [Theory]
    [InlineData("http://objects.example/package")]
    [InlineData("http://10.10.10.10:9000/package")]
    [InlineData("http://192.168.1.5:9000/package")]
    public async Task PlaintextRemoteRequestsFailBeforeInnerHandler(string uri)
    {
        var inner = new RecordingHandler();
        using var client = new HttpClient(new StewardDirectTransferHandler(inner));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(uri));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, inner.Calls);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
