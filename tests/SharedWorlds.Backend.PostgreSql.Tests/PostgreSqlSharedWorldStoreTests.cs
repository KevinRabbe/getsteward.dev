using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlSharedWorldStoreTests : IAsyncLifetime
{
    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    private static readonly DateTimeOffset Now =
        new(2026, 7, 21, 23, 30, 0, TimeSpan.Zero);

    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlSharedWorldStore _store = null!;
    private SharedWorldMetadataService _worlds = null!;
    private SharedRevisionMetadataService _revisions = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("STEWARD_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "STEWARD_TEST_POSTGRES must be set for PostgreSQL integration tests.");
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
        await PostgreSqlBackendSchema.InitializeAsync(_dataSource);
        _store = new PostgreSqlSharedWorldStore(_dataSource);
        _worlds = new SharedWorldMetadataService(_store, () => Now);
        _revisions = new SharedRevisionMetadataService(_store, _store);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task SharedWorldAndManagerPersistAtomicallyAndDuplicateCannotReplaceAuthority()
    {
        await ResetAsync();
        var manager = Steam("76561198000000001");
        var attacker = Steam("76561198000000099");
        var command = NewWorldCommand();

        var first = await _worlds.CreateSharedWorldAsync(manager, command);
        var duplicate = await _worlds.CreateSharedWorldAsync(attacker, command with
        {
            DisplayName = "Hijacked",
            CurrentStateRevisionId = RevisionId.New()
        });

        Assert.Equal(CreateSharedWorldStatus.Created, first.Status);
        Assert.Equal(CreateSharedWorldStatus.AlreadyExists, duplicate.Status);

        var persisted = Assert.IsType<SharedWorldMetadata>(
            await _store.LoadWorldAsync(command.WorldId));
        var member = Assert.IsType<SharedWorldMember>(
            await _store.LoadMemberAsync(command.WorldId, manager.Subject));

        Assert.Equal(manager.Subject, persisted.AccessManager);
        Assert.Equal(command.DisplayName, persisted.DisplayName);
        Assert.Equal(command.CurrentStateRevisionId, persisted.CurrentStateRevisionId);
        Assert.Equal(SharedWorldMemberStatus.Active, member.Status);
        Assert.Null(await _store.LoadMemberAsync(command.WorldId, attacker.Subject));
        Assert.Single(await _worlds.ListAccessibleWorldsAsync(manager));
        Assert.Empty(await _worlds.ListAccessibleWorldsAsync(attacker));
    }

    [Fact]
    public async Task HostedAndNativeEnvironmentMetadataRoundTripWithoutSaveInspection()
    {
        await ResetAsync();
        var manager = Steam("76561198000000001");
        var command = NewWorldCommand();
        await _worlds.CreateSharedWorldAsync(manager, command);
        var environmentId = Assert.IsType<RevisionId>(command.CurrentEnvironmentRevisionId);

        var hosted = new SharedEnvironmentRevisionMetadata(
            command.WorldId,
            environmentId,
            command.AdapterId,
            "objects/world/env-package",
            4096,
            HashA,
            manager.Subject,
            Now);
        Assert.Equal(
            RecordRevisionMetadataStatus.Recorded,
            await _revisions.RecordVerifiedEnvironmentRevisionAsync(hosted));

        var nativeId = RevisionId.New();
        var native = new SharedEnvironmentRevisionMetadata(
            command.WorldId,
            nativeId,
            command.AdapterId,
            "steam-manifest:factorio:stable",
            null,
            null,
            manager.Subject,
            Now.AddMinutes(1));
        Assert.Equal(
            RecordRevisionMetadataStatus.Recorded,
            await _revisions.RecordVerifiedEnvironmentRevisionAsync(native));

        Assert.Equal(
            hosted,
            await _revisions.GetEnvironmentRevisionMetadataAsync(
                manager,
                command.WorldId,
                environmentId));
        Assert.Equal(
            native,
            await _revisions.GetEnvironmentRevisionMetadataAsync(
                manager,
                command.WorldId,
                nativeId));
    }

    [Fact]
    public async Task StateRevisionRequiresRecordedEnvironmentAndImmutableReplayIsDeterministic()
    {
        await ResetAsync();
        var manager = Steam("76561198000000001");
        var command = NewWorldCommand();
        await _worlds.CreateSharedWorldAsync(manager, command);
        var environmentId = Assert.IsType<RevisionId>(command.CurrentEnvironmentRevisionId);
        var state = new SharedStateRevisionMetadata(
            command.WorldId,
            command.CurrentStateRevisionId,
            command.AdapterId,
            "objects/world/state-package",
            8192,
            HashA,
            environmentId,
            manager.Subject,
            Now);

        Assert.Equal(
            RecordRevisionMetadataStatus.RequiredEnvironmentMissing,
            await _revisions.RecordVerifiedStateRevisionAsync(state));

        await _revisions.RecordVerifiedEnvironmentRevisionAsync(new SharedEnvironmentRevisionMetadata(
            command.WorldId,
            environmentId,
            command.AdapterId,
            "objects/world/env-package",
            4096,
            HashA,
            manager.Subject,
            Now));

        Assert.Equal(
            RecordRevisionMetadataStatus.Recorded,
            await _revisions.RecordVerifiedStateRevisionAsync(state));
        Assert.Equal(
            RecordRevisionMetadataStatus.AlreadyRecorded,
            await _revisions.RecordVerifiedStateRevisionAsync(state with
            {
                Sha256 = HashA.ToLowerInvariant(),
                PublishedAt = Now.AddTicks(7)
            }));
        Assert.Equal(
            RecordRevisionMetadataStatus.Conflict,
            await _revisions.RecordVerifiedStateRevisionAsync(state with { Sha256 = HashB }));

        var persisted = Assert.IsType<SharedStateRevisionMetadata>(
            await _revisions.GetStateRevisionMetadataAsync(
                manager,
                command.WorldId,
                state.RevisionId));
        Assert.Equal(HashA, persisted.Sha256);
        Assert.Equal(environmentId, persisted.RequiredEnvironmentRevisionId);
    }

    [Fact]
    public async Task SameOpaqueRevisionIdsRemainIndependentAcrossWorlds()
    {
        await ResetAsync();
        var firstManager = Steam("76561198000000001");
        var secondManager = Steam("76561198000000002");
        var sharedStateId = RevisionId.New();
        var sharedEnvironmentId = RevisionId.New();
        var first = NewWorldCommand() with
        {
            CurrentStateRevisionId = sharedStateId,
            CurrentEnvironmentRevisionId = sharedEnvironmentId
        };
        var second = NewWorldCommand() with
        {
            CurrentStateRevisionId = sharedStateId,
            CurrentEnvironmentRevisionId = sharedEnvironmentId
        };
        await _worlds.CreateSharedWorldAsync(firstManager, first);
        await _worlds.CreateSharedWorldAsync(secondManager, second);

        await RecordEnvironmentAsync(first, firstManager.Subject, "env/first", HashA);
        await RecordEnvironmentAsync(second, secondManager.Subject, "env/second", HashB);
        var firstState = State(first, firstManager.Subject, "state/first", HashA);
        var secondState = State(second, secondManager.Subject, "state/second", HashB);

        Assert.Equal(
            RecordRevisionMetadataStatus.Recorded,
            await _revisions.RecordVerifiedStateRevisionAsync(firstState));
        Assert.Equal(
            RecordRevisionMetadataStatus.Recorded,
            await _revisions.RecordVerifiedStateRevisionAsync(secondState));

        Assert.Equal(
            "state/first",
            (await _revisions.GetStateRevisionMetadataAsync(
                firstManager,
                first.WorldId,
                sharedStateId))?.PackageObjectKey);
        Assert.Equal(
            "state/second",
            (await _revisions.GetStateRevisionMetadataAsync(
                secondManager,
                second.WorldId,
                sharedStateId))?.PackageObjectKey);
        Assert.Null(await _revisions.GetStateRevisionMetadataAsync(
            firstManager,
            second.WorldId,
            sharedStateId));
    }

    [Fact]
    public async Task CurrentRevisionMetadataQueryRemainsMembershipProtectedAfterPersistenceRoundTrip()
    {
        await ResetAsync();
        var manager = Steam("76561198000000001");
        var outsider = Steam("76561198000000099");
        var command = NewWorldCommand();
        await _worlds.CreateSharedWorldAsync(manager, command);
        await RecordEnvironmentAsync(command, manager.Subject, "env/current", HashA);
        await _revisions.RecordVerifiedStateRevisionAsync(
            State(command, manager.Subject, "state/current", HashA));

        var current = Assert.IsType<SharedCurrentRevisionMetadata>(
            await _revisions.GetCurrentRevisionMetadataAsync(manager, command.WorldId));

        Assert.NotNull(current.State);
        Assert.NotNull(current.Environment);
        Assert.Null(await _revisions.GetCurrentRevisionMetadataAsync(outsider, command.WorldId));
    }

    private async Task ResetAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "TRUNCATE TABLE steward_state_revisions, steward_environment_revisions, " +
            "steward_world_members, steward_shared_worlds CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private Task<RecordRevisionMetadataStatus> RecordEnvironmentAsync(
        CreateSharedWorldCommand world,
        ExternalIdentityRef publisher,
        string reference,
        string hash)
    {
        var environmentId = Assert.IsType<RevisionId>(world.CurrentEnvironmentRevisionId);
        return _revisions.RecordVerifiedEnvironmentRevisionAsync(
            new SharedEnvironmentRevisionMetadata(
                world.WorldId,
                environmentId,
                world.AdapterId,
                reference,
                4096,
                hash,
                publisher,
                Now));
    }

    private static SharedStateRevisionMetadata State(
        CreateSharedWorldCommand world,
        ExternalIdentityRef publisher,
        string objectKey,
        string hash)
        => new(
            world.WorldId,
            world.CurrentStateRevisionId,
            world.AdapterId,
            objectKey,
            8192,
            hash,
            world.CurrentEnvironmentRevisionId,
            publisher,
            Now);

    private static CreateSharedWorldCommand NewWorldCommand()
        => new(
            WorldId.New(),
            "factorio",
            "Factory World",
            RevisionId.New(),
            RevisionId.New());

    private static VerifiedExternalIdentity Steam(string id)
        => new(new ExternalIdentityRef("steam", id));
}
