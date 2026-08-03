using System.Net;
using System.Text;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Remote;
using SharedWorlds.Infrastructure.Storage;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class StewardOwnedWorldLocationPublicationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-owned-location-publication-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task NetworkFailureLeavesDurablyStagedOperation()
    {
        var journal = CreateJournal();
        var requestCount = 0;
        using var http = CreateHttp(async (_, _) =>
        {
            requestCount++;
            await Task.Yield();
            throw new HttpRequestException("Injected transport failure.");
        });
        var service = CreateService(journal, http);
        var world = WorldId.New();
        var state = RevisionId.New();
        var environment = RevisionId.New();
        await service.RecordDesiredAsync(world, state, environment);

        await Assert.ThrowsAsync<HttpRequestException>(() => service.ReplayAsync(world));

        var pending = Assert.IsType<OwnedWorldLocationPublicationState>(
            await journal.LoadAsync(world));
        Assert.Equal(1, requestCount);
        Assert.Null(pending.ConfirmedStateRevisionId);
        Assert.Equal(
            OwnedWorldLocationPublicationOperation.Publish(state, environment),
            pending.InFlight);
    }

    [Fact]
    public async Task SuccessfulPublishAcknowledgesExactHead()
    {
        var journal = CreateJournal();
        HttpMethod? method = null;
        string? body = null;
        using var http = CreateHttp(async (request, cancellationToken) =>
        {
            method = request.Method;
            body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json(HttpStatusCode.OK, "WorldLocationCreated");
        });
        var service = CreateService(journal, http);
        var world = WorldId.New();
        var state = RevisionId.New();
        var environment = RevisionId.New();
        await service.RecordDesiredAsync(world, state, environment);

        await service.ReplayAsync(world);

        var confirmed = Assert.IsType<OwnedWorldLocationPublicationState>(
            await journal.LoadAsync(world));
        Assert.Equal(HttpMethod.Put, method);
        Assert.Contains(state.Value.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(environment.Value.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(state, confirmed.ConfirmedStateRevisionId);
        Assert.Equal(environment, confirmed.ConfirmedEnvironmentRevisionId);
        Assert.Null(confirmed.InFlight);
        Assert.True(confirmed.IsSynchronized);
    }

    [Fact]
    public async Task NewDesiredHeadDoesNotReplaceOperationAlreadyOnTheWire()
    {
        var journal = CreateJournal();
        var firstRequestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<string>();
        using var http = CreateHttp(async (request, cancellationToken) =>
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (requests)
            {
                requests.Add(body);
            }

            if (requests.Count == 1)
            {
                firstRequestStarted.SetResult();
                await releaseFirstRequest.Task.WaitAsync(cancellationToken);
                return Json(HttpStatusCode.OK, "WorldLocationCreated");
            }

            return Json(HttpStatusCode.OK, "WorldLocationUpdated");
        });
        var service = CreateService(journal, http);
        var world = WorldId.New();
        var firstState = RevisionId.New();
        var firstEnvironment = RevisionId.New();
        var newestState = RevisionId.New();
        var newestEnvironment = RevisionId.New();
        await service.RecordDesiredAsync(world, firstState, firstEnvironment);

        var replay = service.ReplayAsync(world);
        await firstRequestStarted.Task;
        await service.RecordDesiredAsync(world, newestState, newestEnvironment);
        releaseFirstRequest.SetResult();
        await replay;

        var confirmed = Assert.IsType<OwnedWorldLocationPublicationState>(
            await journal.LoadAsync(world));
        Assert.Equal(2, requests.Count);
        Assert.Contains(firstState.Value.ToString("D"), requests[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(newestState.Value.ToString("D"), requests[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            firstState.Value.ToString("D"),
            requests[1],
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(newestState, confirmed.ConfirmedStateRevisionId);
        Assert.Equal(newestEnvironment, confirmed.ConfirmedEnvironmentRevisionId);
        Assert.True(confirmed.IsSynchronized);
    }

    [Fact]
    public async Task RemovalPreservesAndFinishesAmbiguousFirstPublish()
    {
        var journal = CreateJournal();
        using (var failingHttp = CreateHttp((_, _) =>
                   throw new HttpRequestException("Injected ambiguous publish.")))
        {
            var failingService = CreateService(journal, failingHttp);
            var world = WorldId.New();
            var state = RevisionId.New();
            var environment = RevisionId.New();
            await failingService.RecordDesiredAsync(world, state, environment);
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                failingService.ReplayAsync(world));
            await failingService.RecordRemovalAsync(world);

            var methods = new List<HttpMethod>();
            using var recoveryHttp = CreateHttp((request, _) =>
            {
                methods.Add(request.Method);
                return Task.FromResult(request.Method == HttpMethod.Put
                    ? Json(HttpStatusCode.OK, "WorldLocationCreated")
                    : Json(HttpStatusCode.OK, "WorldLocationUpdated"));
            });
            var recoveryService = CreateService(journal, recoveryHttp);

            await recoveryService.ReplayAsync(world);

            Assert.Equal([HttpMethod.Put, HttpMethod.Delete], methods);
            Assert.Null(await journal.LoadAsync(world));
        }
    }

    [Fact]
    public async Task RemovalBeforeAnyNetworkAttemptDropsUnneededJournalEntry()
    {
        var journal = CreateJournal();
        using var http = CreateHttp((_, _) =>
            throw new InvalidOperationException("Network must not be called."));
        var service = CreateService(journal, http);
        var world = WorldId.New();
        await service.RecordDesiredAsync(world, RevisionId.New(), RevisionId.New());

        await service.RecordRemovalAsync(world);

        Assert.Null(await journal.LoadAsync(world));
    }

    [Fact]
    public async Task ConflictPreservesExactInFlightCasForExplicitResolution()
    {
        var journal = CreateJournal();
        using var http = CreateHttp((_, _) => Task.FromResult(
            Json(HttpStatusCode.Conflict, "WorldLocationConflict")));
        var service = CreateService(journal, http);
        var world = WorldId.New();
        await service.RecordDesiredAsync(world, RevisionId.New(), RevisionId.New());

        var exception = await Assert.ThrowsAsync<StewardOwnedWorldLocationPublicationException>(
            () => service.ReplayAsync(world));

        Assert.Equal(world, exception.WorldId);
        Assert.Equal("WorldLocationConflict", exception.Code);
        Assert.False(exception.Retryable);
        Assert.NotNull((await journal.LoadAsync(world))!.InFlight);
    }

    [Fact]
    public async Task UnexpectedSuccessCodeFailsClosedWithoutAcknowledgement()
    {
        var journal = CreateJournal();
        using var http = CreateHttp((_, _) => Task.FromResult(
            Json(HttpStatusCode.OK, "InstallationRegistered")));
        var service = CreateService(journal, http);
        var world = WorldId.New();
        await service.RecordDesiredAsync(world, RevisionId.New(), RevisionId.New());

        var exception = await Assert.ThrowsAsync<StewardOwnedWorldLocationPublicationException>(
            () => service.ReplayAsync(world));

        Assert.Equal("InstallationRegistered", exception.Code);
        var pending = Assert.IsType<OwnedWorldLocationPublicationState>(
            await journal.LoadAsync(world));
        Assert.Null(pending.ConfirmedStateRevisionId);
        Assert.NotNull(pending.InFlight);
    }

    private LocalOwnedWorldLocationPublicationJournal CreateJournal()
        => new(_root);

    private static StewardOwnedWorldLocationPublicationService CreateService(
        IOwnedWorldLocationPublicationJournal journal,
        HttpClient http)
        => new(
            journal,
            new StewardOwnedWorldLocationClient(
                http,
                _ => Task.FromResult<string?>("access-token")),
            () => new DateTimeOffset(2026, 8, 3, 20, 0, 0, TimeSpan.Zero));

    private static HttpClient CreateHttp(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        => new(new AsyncStubHandler(handler))
        {
            BaseAddress = new Uri("https://safe.example/")
        };

    private static HttpResponseMessage Json(HttpStatusCode status, string code)
        => new(status)
        {
            Content = new StringContent(
                $"{{\"code\":\"{code}\",\"retryable\":false}}",
                Encoding.UTF8,
                "application/json")
        };

    private sealed class AsyncStubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public AsyncStubHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Test cleanup must not hide the actual assertion result.
        }
        catch (UnauthorizedAccessException)
        {
            // Test cleanup must not hide the actual assertion result.
        }
    }
}
