using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class LocalOwnedWorldLocationPublicationJournalTests : IDisposable
{
    private const string InstallationId = "desktop-installation-a";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sharedworlds-owned-location-journal-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task PublicationState_RoundTripsAndCanBeRemoved()
    {
        var journal = new LocalOwnedWorldLocationPublicationJournal(_root);
        var state = CreatePendingPublish();

        await journal.SaveAsync(state);

        Assert.Equal(state, await journal.LoadAsync(state.WorldId));
        Assert.Equal(state, Assert.Single(await journal.ListAsync()));

        await journal.RemoveAsync(state.WorldId);
        Assert.Null(await journal.LoadAsync(state.WorldId));
        Assert.Empty(await journal.ListAsync());
    }

    [Fact]
    public async Task PublicationState_IsStoredInProtectedInstallationBoundEnvelope()
    {
        var journal = new LocalOwnedWorldLocationPublicationJournal(_root);
        var state = CreatePendingPublish();
        await journal.SaveAsync(state);

        await using var stream = File.OpenRead(GetPath(state.WorldId));
        using var document = await JsonDocument.ParseAsync(stream);
        var root = document.RootElement;

        Assert.Equal(
            "sharedworlds.owned-world-location-publication",
            root.GetProperty("documentType").GetString());
        Assert.Equal(4, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("integrityVersion").GetInt32());
        Assert.Equal(64, root.GetProperty("contentSha256").GetString()!.Length);
        Assert.Equal(
            InstallationId,
            root.GetProperty("payload").GetProperty("installationId").GetString());
    }

    [Fact]
    public async Task UnboundSchemaTwoJournalFailsClosed()
    {
        var journal = new LocalOwnedWorldLocationPublicationJournal(_root);
        var state = CreatePendingPublish();
        await journal.SaveAsync(state);

        var path = GetPath(state.WorldId);
        var envelope = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        const string documentType = "sharedworlds.owned-world-location-publication";
        const int schemaVersion = 2;
        var payload = envelope["payload"]!.AsObject();
        payload.Remove("installationId");
        envelope["schemaVersion"] = schemaVersion;
        envelope["contentSha256"] = ComputeContentSha256(
            documentType,
            schemaVersion,
            payload);
        await File.WriteAllTextAsync(path, envelope.ToJsonString());

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => journal.LoadAsync(state.WorldId));

        Assert.Contains(
            "predates exact installation-ID binding",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DesiredHeadMayAdvanceWithoutReplacingInFlightCasOperation()
    {
        var journal = new LocalOwnedWorldLocationPublicationJournal(_root);
        var worldId = WorldId.New();
        var firstState = RevisionId.New();
        var firstEnvironment = RevisionId.New();
        var newestState = RevisionId.New();
        var newestEnvironment = RevisionId.New();
        var inFlight = OwnedWorldLocationPublicationOperation.Publish(
            firstState,
            firstEnvironment);
        var state = new OwnedWorldLocationPublicationState(
            worldId,
            InstallationId,
            newestState,
            newestEnvironment,
            ConfirmedStateRevisionId: null,
            ConfirmedEnvironmentRevisionId: null,
            inFlight,
            DateTimeOffset.UtcNow);

        await journal.SaveAsync(state);
        var loaded = Assert.IsType<OwnedWorldLocationPublicationState>(
            await journal.LoadAsync(worldId));

        Assert.Equal(InstallationId, loaded.InstallationId);
        Assert.Equal(newestState, loaded.DesiredStateRevisionId);
        Assert.Equal(newestEnvironment, loaded.DesiredEnvironmentRevisionId);
        Assert.Equal(inFlight, loaded.InFlight);
        Assert.False(loaded.IsSynchronized);
    }

    [Fact]
    public async Task RemovalRequestPreservesAmbiguousInFlightPublish()
    {
        var journal = new LocalOwnedWorldLocationPublicationJournal(_root);
        var worldId = WorldId.New();
        var headState = RevisionId.New();
        var headEnvironment = RevisionId.New();
        var state = new OwnedWorldLocationPublicationState(
            worldId,
            InstallationId,
            DesiredStateRevisionId: null,
            DesiredEnvironmentRevisionId: null,
            ConfirmedStateRevisionId: null,
            ConfirmedEnvironmentRevisionId: null,
            OwnedWorldLocationPublicationOperation.Publish(
                headState,
                headEnvironment),
            DateTimeOffset.UtcNow);

        await journal.SaveAsync(state);
        var loaded = Assert.IsType<OwnedWorldLocationPublicationState>(
            await journal.LoadAsync(worldId));

        Assert.True(loaded.RemovalRequested);
        Assert.Equal(
            OwnedWorldLocationPublicationOperationKind.Publish,
            loaded.InFlight!.Kind);
        Assert.Equal(headState, loaded.InFlight.StateRevisionId);
        Assert.Equal(headEnvironment, loaded.InFlight.EnvironmentRevisionId);
    }

    [Fact]
    public void PartialDesiredPairFailsClosedBeforePersistence()
    {
        var state = new OwnedWorldLocationPublicationState(
            WorldId.New(),
            InstallationId,
            RevisionId.New(),
            DesiredEnvironmentRevisionId: null,
            ConfirmedStateRevisionId: null,
            ConfirmedEnvironmentRevisionId: null,
            InFlight: null,
            DateTimeOffset.UtcNow);

        var exception = Assert.Throws<InvalidDataException>(state.Validate);

        Assert.Contains(
            "Desired state and environment revisions",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidInstallationIdsFailClosedBeforePersistence()
    {
        var desiredState = RevisionId.New();
        var desiredEnvironment = RevisionId.New();

        InvalidDataException Validate(string installationId)
        {
            var state = new OwnedWorldLocationPublicationState(
                WorldId.New(),
                installationId,
                desiredState,
                desiredEnvironment,
                ConfirmedStateRevisionId: null,
                ConfirmedEnvironmentRevisionId: null,
                InFlight: null,
                DateTimeOffset.UtcNow);
            return Assert.Throws<InvalidDataException>(state.Validate);
        }

        Assert.Contains(
            "durable installation ID",
            Validate(" ").Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "must not exceed 128",
            Validate(new string('a', 129)).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "control characters",
            Validate("desktop\ninstallation").Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InFlightExpectationMustMatchConfirmedHead()
    {
        var confirmedState = RevisionId.New();
        var confirmedEnvironment = RevisionId.New();
        var state = new OwnedWorldLocationPublicationState(
            WorldId.New(),
            InstallationId,
            RevisionId.New(),
            RevisionId.New(),
            confirmedState,
            confirmedEnvironment,
            OwnedWorldLocationPublicationOperation.Publish(
                RevisionId.New(),
                RevisionId.New(),
                expectedStateRevisionId: RevisionId.New(),
                expectedEnvironmentRevisionId: RevisionId.New()),
            DateTimeOffset.UtcNow);

        var exception = Assert.Throws<InvalidDataException>(state.Validate);

        Assert.Contains(
            "in-flight CAS expectation",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MismatchedWorldStorageKeyFailsClosed()
    {
        var journal = new LocalOwnedWorldLocationPublicationJournal(_root);
        var state = CreatePendingPublish();
        await journal.SaveAsync(state);

        var mismatchedPath = GetPath(WorldId.New());
        File.Move(GetPath(state.WorldId), mismatchedPath);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => journal.ListAsync());

        Assert.Contains("mismatched World key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyStateCannotBecomeDurableAuthority()
    {
        var state = new OwnedWorldLocationPublicationState(
            WorldId.New(),
            InstallationId,
            DesiredStateRevisionId: null,
            DesiredEnvironmentRevisionId: null,
            ConfirmedStateRevisionId: null,
            ConfirmedEnvironmentRevisionId: null,
            InFlight: null,
            DateTimeOffset.UtcNow);

        var exception = Assert.Throws<InvalidDataException>(state.Validate);

        Assert.Contains("no durable work or evidence", exception.Message, StringComparison.Ordinal);
    }

    private OwnedWorldLocationPublicationState CreatePendingPublish()
    {
        var confirmedState = RevisionId.New();
        var confirmedEnvironment = RevisionId.New();
        return new(
            WorldId.New(),
            InstallationId,
            DesiredStateRevisionId: RevisionId.New(),
            DesiredEnvironmentRevisionId: confirmedEnvironment,
            confirmedState,
            confirmedEnvironment,
            OwnedWorldLocationPublicationOperation.Publish(
                RevisionId.New(),
                confirmedEnvironment,
                confirmedState,
                confirmedEnvironment),
            DateTimeOffset.UtcNow);
    }

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
