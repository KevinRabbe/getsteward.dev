using System.Net;
using System.Text;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests.Remote;

public sealed class StewardPackageDownloadClientTests
{
    [Theory]
    [InlineData("../escape")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    [InlineData("ABCDEF")]
    public async Task MalformedSha256AuthorizationFailsBeforeCacheUse(string sha256)
    {
        using var apiClient = CreateApiClient(sha256);
        var client = new StewardPackageDownloadClient(apiClient);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.AuthorizeDownloadAsync(
                WorldId.New(),
                RevisionId.New(),
                RemotePackageKind.State,
                "access-token"));

        Assert.Contains("inconsistent immutable package metadata", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidLowercaseSha256IsNormalizedBeforeCacheUse()
    {
        var lowercaseSha = new string('a', 64);
        using var apiClient = CreateApiClient(lowercaseSha);
        var client = new StewardPackageDownloadClient(apiClient);

        var authorization = await client.AuthorizeDownloadAsync(
            WorldId.New(),
            RevisionId.New(),
            RemotePackageKind.State,
            "access-token");

        Assert.Equal(new string('A', 64), authorization.ExpectedSha256);
        Assert.Equal(4, authorization.ExpectedByteSize);
    }

    private static HttpClient CreateApiClient(string sha256)
    {
        var handler = new DelegateHandler((_, _) =>
        {
            var json = $$"""
            {
              "code": "DownloadAuthorized",
              "data": {
                "authorization": {
                  "uri": "https://objects.example/package",
                  "method": "GET",
                  "requiredHeaders": {},
                  "expiresAt": "{{DateTimeOffset.UtcNow.AddMinutes(10):O}}",
                  "expectedByteSize": 4
                },
                "expectedByteSize": 4,
                "expectedSha256": "{{sha256}}"
              },
              "retryable": false
            }
            """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        });

        return new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.example/")
        };
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }
}
