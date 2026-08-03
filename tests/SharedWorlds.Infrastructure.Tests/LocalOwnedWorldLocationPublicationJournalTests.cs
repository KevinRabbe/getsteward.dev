using System.Text.Json;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;
using SharedWorlds.Infrastructure.Storage;

namespace SharedWorlds.Infrastructure.Tests;

public sealed class LocalOwnedWorldLocationPublicationJournalTests : IDisposable
{
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
    public async Task PublicationState_IsStoredInProtectedEnvelope()
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
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("integrityVersion").GetInt32());
        Assert.Equal(64, root.GetProperty("contentSha256").GetString()!.Length);
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
            newestState,
            newestEnvironment,
            ConfirmedStateRevisionId: null,
            ConfirmedEnvironmentRevisionId: null,
            inFlight,
            DateTimeOffset.UtcNow);

        await journal.SaveAsync(state);
        var loaded = Assert.IsType<OwnedWorldLocationPublicationState>(
            await journal.LoadAsync(worldId));

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
    public void InFlightExpectationMustMatchConfirmedHead()
    {
        var confirmedState = RevisionId.New();
        var confirmedEnvironment = RevisionId.New();
        var state = new OwnedWorldLocationPublicationState(
            WorldId.New(),
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
