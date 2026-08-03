using System.Net;
using System.Text;
using SharedWorlds.Core.Domain;
using SharedWorlds.Infrastructure.Remote;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class StewardOwnedWorldLocationClientTests
{
    [Fact]
    public async Task ResolveUsesBearerAuthenticationAndExactRoute()
    {
        HttpRequestMessage? captured = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, "{\"code\":\"BringHereAvailability\",\"retryable\":false,\"data\":{}}");
        }))
        {
            BaseAddress = new Uri("https://safe.example/")
        };
        var client = new StewardOwnedWorldLocationClient(http, _ => Task.FromResult<string?>("token-1"));
        var worldId = WorldId.New();

        var response = await client.ResolveBringHereAsync(worldId);

        Assert.Equal("BringHereAvailability", response.Code);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal($"https://safe.example/api/v1/private-worlds/{worldId.Value:D}/bring-here", captured.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("token-1", captured.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task RemoveUsesQueryCasAndNoBody()
    {
        HttpRequestMessage? captured = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, "{\"code\":\"WorldLocationUpdated\",\"retryable\":false}");
        }))
        {
            BaseAddress = new Uri("https://safe.example/")
        };
        var client = new StewardOwnedWorldLocationClient(http, _ => Task.FromResult<string?>("token"));
        var world = WorldId.New();
        var state = RevisionId.New();
        var environment = RevisionId.New();

        await client.RemoveCurrentLocationAsync(world, state, environment);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Delete, captured.Method);
        Assert.Null(captured.Content);
        Assert.Contains($"expectedStateRevisionId={state.Value:D}", captured.RequestUri!.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"expectedEnvironmentRevisionId={environment.Value:D}", captured.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConflictIsReturnedWithoutThrowing()
    {
        using var http = new HttpClient(new StubHandler(_ =>
            Json(HttpStatusCode.Conflict, "{\"code\":\"WorldLocationConflict\",\"retryable\":false,\"data\":{}}")))
        {
            BaseAddress = new Uri("https://safe.example/")
        };
        var client = new StewardOwnedWorldLocationClient(http, _ => Task.FromResult<string?>("token"));

        var response = await client.PublishCurrentLocationAsync(
            WorldId.New(),
            RevisionId.New(),
            RevisionId.New());

        Assert.True(response.IsConflict);
        Assert.Equal("WorldLocationConflict", response.Code);
    }

    [Fact]
    public void HttpBackendIsRejected()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException()))
        {
            BaseAddress = new Uri("http://unsafe.example/")
        };

        Assert.Throws<ArgumentException>(() =>
            new StewardOwnedWorldLocationClient(http, _ => Task.FromResult<string?>("token")));
    }

    [Fact]
    public async Task MissingSessionFailsBeforeNetwork()
    {
        var called = false;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            called = true;
            throw new InvalidOperationException();
        }))
        {
            BaseAddress = new Uri("https://safe.example/")
        };
        var client = new StewardOwnedWorldLocationClient(http, _ => Task.FromResult<string?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ResolveBringHereAsync(WorldId.New()));
        Assert.False(called);
    }

    [Fact]
    public async Task OversizedResponseIsRejected()
    {
        var payload = "{\"code\":\"x\",\"retryable\":false,\"data\":\"" + new string('a', 1024 * 1024) + "\"}";
        using var http = new HttpClient(new StubHandler(_ =>
            Json(HttpStatusCode.OK, payload)))
        {
            BaseAddress = new Uri("https://safe.example/")
        };
        var client = new StewardOwnedWorldLocationClient(http, _ => Task.FromResult<string?>("token"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.ResolveBringHereAsync(WorldId.New()));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
