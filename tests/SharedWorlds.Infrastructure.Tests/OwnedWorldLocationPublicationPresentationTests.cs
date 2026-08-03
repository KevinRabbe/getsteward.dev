using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Remote;
using SharedWorlds.Infrastructure.Storage;
using Xunit;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class OwnedWorldLocationPublicationPresentationTests : IDisposable
{
    private const string InstallationId = "desktop-installation-a";

    private static readonly DateTimeOffset ObservedAt =
        new(2026, 8, 3, 20, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-publication-presentation-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task SchemaFourPersistsPresentationAndSchemaThreeMigratesToNull()
    {
        var journal = CreateJournal();
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var presentation = new OwnedWorldPresentation("Factory World", "factorio");
        var state = new OwnedWorldLocationPublicationState(
            worldId,
            InstallationId,
            stateId,
            environmentId,
            ConfirmedStateRevisionId: null,
            ConfirmedEnvironmentRevisionId: null,
            InFlight: null,
            ObservedAt,
            DesiredPresentation: presentation,
            ConfirmedPresentation: null);
        await journal.SaveAsync(state);

        var path = GetPath(worldId);
        var envelope = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.Equal(4, envelope["schemaVersion"]!.GetValue<int>());
        Assert.Equal(
            "Factory World",
            envelope["payload"]!["desiredPresentation"]!["name"]!.GetValue<string>());

        const string documentType = "sharedworlds.owned-world-location-publication";
        const int legacyVersion = 3;
        var payload = envelope["payload"]!.AsObject();
        payload.Remove("desiredPresentation");
        payload.Remove("confirmedPresentation");
        envelope["schemaVersion"] = legacyVersion;
        envelope["contentSha256"] = ComputeContentSha256(
            documentType,
            legacyVersion,
            payload);
        await File.WriteAllTextAsync(path, envelope.ToJsonString());

        var migrated = Assert.IsType<OwnedWorldLocationPublicationState>(
            await journal.LoadAsync(worldId));
        Assert.Null(migrated.DesiredPresentation);
        Assert.Null(migrated.ConfirmedPresentation);
        Assert.Equal(stateId, migrated.DesiredStateRevisionId);
        Assert.False(migrated.IsSynchronized);
    }

    [Fact]
    public async Task SameHeadPresentationChangeBecomesExactReplayWork()
    {
        var journal = CreateJournal();
        var bodies = new List<string>();
        using var http = CreateHttp(async (request, cancellationToken) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return Json(bodies.Count == 1
                ? "WorldLocationCreated"
                : "WorldLocationUnchanged");
        });
        var service = CreateService(journal, http);
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();

        await service.RecordDesiredWithPresentationAsync(
            worldId,
            stateId,
            environmentId,
            "Factory World",
            "factorio");
        await service.ReplayAsync(worldId);
        await service.RecordDesiredWithPresentationAsync(
            worldId,
            stateId,
            environmentId,
            "Factory World Renamed",
            "factorio");
        await service.ReplayAsync(worldId);

        Assert.Equal(2, bodies.Count);
        Assert.Contains("Factory World", bodies[0], StringComparison.Ordinal);
        Assert.Contains("Factory World Renamed", bodies[1], StringComparison.Ordinal);
        Assert.Contains(stateId.Value.ToString("D"), bodies[1], StringComparison.OrdinalIgnoreCase);
        var confirmed = Assert.IsType<OwnedWorldLocationPublicationState>(
            await journal.LoadAsync(worldId));
        Assert.Equal(
            new OwnedWorldPresentation("Factory World Renamed", "factorio"),
            confirmed.ConfirmedPresentation);
        Assert.True(confirmed.IsSynchronized);
    }

    [Fact]
    public async Task PresentationChangeCannotReplaceOperationAlreadyOnTheWire()
    {
        var journal = CreateJournal();
        var firstStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var bodies = new List<string>();
        using var http = CreateHttp(async (request, cancellationToken) =>
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            bodies.Add(body);
            if (bodies.Count == 1)
            {
                firstStarted.SetResult(true);
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return Json("WorldLocationCreated");
            }

            return Json("WorldLocationUnchanged");
        });
        var service = CreateService(journal, http);
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        await service.RecordDesiredWithPresentationAsync(
            worldId,
            stateId,
            environmentId,
            "Original Name",
            "factorio");

        var replay = service.ReplayAsync(worldId);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.RecordDesiredWithPresentationAsync(
            worldId,
            stateId,
            environmentId,
            "Renamed World",
            "factorio");
        releaseFirst.SetResult(true);
        await replay;

        Assert.Equal(2, bodies.Count);
        Assert.Contains("Original Name", bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Renamed World", bodies[0], StringComparison.Ordinal);
        Assert.Contains("Renamed World", bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task MigratedPresentationlessInFlightOperationReplaysLegacyRequest()
    {
        var journal = CreateJournal();
        var worldId = WorldId.New();
        var stateId = RevisionId.New();
        var environmentId = RevisionId.New();
        var operation = OwnedWorldLocationPublicationOperation.Publish(
            stateId,
            environmentId);
        await journal.SaveAsync(new OwnedWorldLocationPublicationState(
            worldId,
            InstallationId,
            stateId,
            environmentId,
            ConfirmedStateRevisionId: null,
            ConfirmedEnvironmentRevisionId: null,
            operation,
            ObservedAt));
        string? body = null;
        using var http = CreateHttp(async (request, cancellationToken) =>
        {
            body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Json("WorldLocationCreated");
        });
        var service = CreateService(journal, http);

        await service.ReplayAsync(worldId);

        Assert.NotNull(body);
        Assert.DoesNotContain("worldName", body, StringComparison.Ordinal);
        Assert.DoesNotContain("gameAdapterId", body, StringComparison.Ordinal);
        var confirmed = Assert.IsType<OwnedWorldLocationPublicationState>(
            await journal.LoadAsync(worldId));
        Assert.Null(confirmed.ConfirmedPresentation);
        Assert.True(confirmed.IsSynchronized);
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
            InstallationId,
            () => ObservedAt);

    private static HttpClient CreateHttp(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        => new(new AsyncStubHandler(handler))
        {
            BaseAddress = new Uri("https://safe.example/")
        };

    private static HttpResponseMessage Json(string code)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"code\":\"{code}\",\"retryable\":false}}",
                Encoding.UTF8,
                "application/json")
        };

    private static string ComputeContentSha256(
        string documentType,
        int schemaVersion,
        JsonNode payload)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("documentType", documentType);
            writer.WriteNumber("schemaVersion", schemaVersion);
            writer.WritePropertyName("payload");
            payload.WriteTo(writer);
            writer.WriteEndObject();
            writer.Flush();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private string GetPath(WorldId worldId)
        => Path.Combine(
            _root,
            "owned-world-location-publication",
            $"{worldId}.json");

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
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
