using SharedWorlds.Backend.Identity;
using Xunit;

namespace SharedWorlds.Backend.Tests;

public sealed class SteamWebApiTicketVerifierUnavailableTests
{
    [Fact]
    public async Task UnconfiguredVerifierFailsClosedWithoutNetworkIo()
    {
        var handler = new ThrowingHandler();
        using var httpClient = new HttpClient(handler);
        var verifier = SteamWebApiTicketVerifier.CreateUnavailable(httpClient);

        var exception = await Assert.ThrowsAsync<ExternalIdentityProviderException>(() =>
            verifier.VerifyAsync("AABBCC"));

        Assert.Equal("steam", exception.Provider);
        Assert.Contains("not configured", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.SendCount);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            throw new InvalidOperationException("Network must not be used when Steam authentication is disabled.");
        }
    }
}
