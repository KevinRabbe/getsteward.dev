using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlSharedWorldAuthorityStoreTests : IAsyncLifetime
{
    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly SharedWorldAuthorityOptions Options =
        SharedWorldAuthorityOptions.FirstReleaseDefaults;

    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlSharedWorldStore _worldStore = null!;
    private SharedWorldMetadataService _worlds = null!;
    private SharedRevisionMetadataService _revisions = null!;
    private PostgreSqlSharedWorldAuthorityStore _authority = null!;
    private VerifiedExternalIdentity _manager = null!;
    private SharedWorldMetadata _world = null!;

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
        await ResetAsync();

        _worldStore = new PostgreSqlSharedWorldStore(_dataSource);
        _worlds = new SharedWorldMetadataService(_worldStore, () => Now);
        _revisions = new SharedRevisionMetadataService(_worldStore, _worldStore);
        _authority = new PostgreSqlSharedWorldAuthorityStore(_dataSource);
        _manager = Steam("76561198000000001");

        var created = await _worlds.CreateSharedWorldAsync(
            _manager,
            new CreateSharedWorldCommand(
                WorldId.New(),
                "factorio",
                "Authority World",
                RevisionId.New(),
                null));
        _world = Assert.IsType<SharedWorldMetadata>(created.World);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task ParallelAcquireAllowsExactlyOneWriter()
    {
        var member = Steam("76561198000000002");
        await AddActiveMemberAsync(member.Subject);
        var head = Head(_world);

        var first = _authority.AcquireAsync(
            _manager.Subject,
            _world.WorldId,
            "device-a",
            head,
            Now,
            Options);
        var second = _authority.AcquireAsync(
            member.Subject,
            _world.WorldId,
            "device-b",
            head,
            Now,
            Options);

        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Status == AcquireSharedWorldReservationStatus.Acquired);
        Assert.Single(results, result => result.Status == AcquireSharedWorldReservationStatus.WorldBusy);
        var reservation = Assert.IsType<SharedWorldReservation>(
            await _authority.GetReservationAsync(_manager.Subject, _world.WorldId, Now, Options));
        Assert.Equal(1, reservation.Generation);
    }

    [Fact]
    public async Task AcquireRequiresActiveMembershipAndExactHead()
    {
        var stranger = Steam("76561198000000099");
        var head = Head(_world);

        var unauthorized = await _authority.AcquireAsync(
            stranger.Subject,
            _world.WorldId,
            "stranger-device",
            head,
            Now,
            Options);
        Assert.Equal(AcquireSharedWorldReservationStatus.NotFoundOrUnauthorized, unauthorized.Status);

        var stale = await _authority.AcquireAsync(
            _manager.Subject,
            _world.WorldId,
            "device-a",
            new SharedWorldHead(RevisionId.New(), null),
            Now,
            Options);
        Assert.Equal(AcquireSharedWorldReservationStatus.HeadChanged, stale.Status);
        Assert.Equal(head, stale.CurrentHead);
    }

    [Fact]
    public async Task SilenceBecomesUncertainAndSameGenerationCanReconnect()
    {
        var acquired = await AcquireManagerAsync();
        var reservation = Assert.IsType<SharedWorldReservation>(acquired.Reservation);

        var uncertain = Assert.IsType<SharedWorldReservation>(
            await _authority.GetReservationAsync(
                _manager.Subject,
                _world.WorldId,
                Now + Options.UncertaintyAfter,
                Options));
        Assert.Equal(SharedWorldReservationState.Uncertain, uncertain.State);
        Assert.Equal(Now + Options.UncertaintyAfter, uncertain.BecameUncertainAt);

        Assert.Equal(
            SharedWorldHeartbeatStatus.ReservationMismatch,
            await _authority.HeartbeatAsync(
                _manager.Subject,
                _world.WorldId,
                "device-a",
                reservation.SessionId,
                reservation.Generation + 1,
                Now + Options.UncertaintyAfter + TimeSpan.FromSeconds(5),
                Options));

        Assert.Equal(
            SharedWorldHeartbeatStatus.Accepted,
            await _authority.HeartbeatAsync(
                _manager.Subject,
                _world.WorldId,
                "device-a",
                reservation.SessionId,
                reservation.Generation,
                Now + Options.UncertaintyAfter + TimeSpan.FromSeconds(5),
                Options));

        var reconnected = Assert.IsType<SharedWorldReservation>(
            await _authority.GetReservationAsync(
                _manager.Subject,
                _world.WorldId,
                Now + Options.UncertaintyAfter + TimeSpan.FromSeconds(5),
                Options));
        Assert.Equal(SharedWorldReservationState.Active, reconnected.State);
        Assert.Null(reconnected.BecameUncertainAt);
    }

    [Fact]
    public async Task ReclaimRequiresGraceAndNextAcquireUsesHigherGeneration()
    {
        var member = Steam("76561198000000002");
        await AddActiveMemberAsync(member.Subject);
        var acquired = await AcquireManagerAsync();
        var first = Assert.IsType<SharedWorldReservation>(acquired.Reservation);
        var uncertainAt = Now + Options.UncertaintyAfter;

        Assert.NotNull(await _authority.GetReservationAsync(
            member.Subject,
            _world.WorldId,
            uncertainAt,
            Options));

        var tooEarly = await _authority.ReclaimAsync(
            member.Subject,
            _world.WorldId,
            first.SessionId,
            first.Generation,
            uncertainAt + Options.ReclaimAfterUncertain - TimeSpan.FromSeconds(1),
            Options);
        Assert.Equal(ReclaimSharedWorldReservationStatus.GracePeriodRequired, tooEarly.Status);

        var reclaimed = await _authority.ReclaimAsync(
            member.Subject,
            _world.WorldId,
            first.SessionId,
            first.Generation,
            uncertainAt + Options.ReclaimAfterUncertain,
            Options);
        Assert.Equal(ReclaimSharedWorldReservationStatus.Reclaimed, reclaimed.Status);
        Assert.Equal(first.Generation, reclaimed.InvalidatedGeneration);

        Assert.Equal(
            SharedWorldHeartbeatStatus.ReservationMismatch,
            await _authority.HeartbeatAsync(
                _manager.Subject,
                _world.WorldId,
                "device-a",
                first.SessionId,
                first.Generation,
                uncertainAt + Options.ReclaimAfterUncertain,
                Options));

        var secondAcquire = await _authority.AcquireAsync(
            member.Subject,
            _world.WorldId,
            "device-b",
            Head(_world),
            uncertainAt + Options.ReclaimAfterUncertain,
            Options);
        var second = Assert.IsType<SharedWorldReservation>(secondAcquire.Reservation);
        Assert.Equal(first.Generation + 1, second.Generation);
    }

    [Fact]
    public async Task RevocationPendingWriterCanHeartbeatAndCommitSafely()
    {
        var acquired = await AcquireManagerAsync();
        var reservation = Assert.IsType<SharedWorldReservation>(acquired.Reservation);
        Assert.True(await _authority.HasUnresolvedWritableResponsibilityAsync(
            _world.WorldId,
            _manager.Subject));
        await SetMemberStatusAsync(_manager.Subject, SharedWorldMemberStatus.RevocationPending);

        Assert.Equal(
            SharedWorldHeartbeatStatus.Accepted,
            await _authority.HeartbeatAsync(
                _manager.Subject,
                _world.WorldId,
                "device-a",
                reservation.SessionId,
                reservation.Generation,
                Now.AddSeconds(30),
                Options));

        var candidate = RevisionId.New();
        await RecordStateAsync(candidate, HashB, requiredEnvironment: null);
        var committed = await _authority.CommitAsync(
            _manager.Subject,
            new CommitSharedWorldCommand(
                _world.WorldId,
                reservation.SessionId,
                reservation.Generation,
                "device-a",
                reservation.StartingHead,
                candidate,
                null),
            Now.AddMinutes(1),
            Options);

        Assert.Equal(CommitSharedWorldStatus.Committed, committed.Status);
        Assert.Equal(candidate, committed.CurrentHead.StateRevisionId);
        Assert.False(await _authority.HasUnresolvedWritableResponsibilityAsync(
            _world.WorldId,
            _manager.Subject));
    }

    [Fact]
    public async Task CommitAdvancesStateAndEnvironmentAtomically()
    {
        var acquired = await AcquireManagerAsync();
        var reservation = Assert.IsType<SharedWorldReservation>(acquired.Reservation);
        var environment = RevisionId.New();
        await RecordEnvironmentAsync(environment, HashA);
        var state = RevisionId.New();
        await RecordStateAsync(state, HashB, environment);

        var result = await _authority.CommitAsync(
            _manager.Subject,
            new CommitSharedWorldCommand(
                _world.WorldId,
                reservation.SessionId,
                reservation.Generation,
                "device-a",
                reservation.StartingHead,
                state,
                environment),
            Now.AddMinutes(1),
            Options);

        Assert.Equal(CommitSharedWorldStatus.Committed, result.Status);
        Assert.Equal(new SharedWorldHead(state, environment), result.CurrentHead);
        var persisted = Assert.IsType<SharedWorldMetadata>(
            await _worlds.GetAccessibleWorldAsync(_manager, _world.WorldId));
        Assert.Equal(state, persisted.CurrentStateRevisionId);
        Assert.Equal(environment, persisted.CurrentEnvironmentRevisionId);
        Assert.Null(await _authority.GetReservationAsync(
            _manager.Subject,
            _world.WorldId,
            Now.AddMinutes(1),
            Options));
    }

    [Fact]
    public async Task RequiredEnvironmentMismatchRejectsCandidateAndKeepsReservation()
    {
        var acquired = await AcquireManagerAsync();
        var reservation = Assert.IsType<SharedWorldReservation>(acquired.Reservation);
        var required = RevisionId.New();
        var wrong = RevisionId.New();
        await RecordEnvironmentAsync(required, HashA);
        await RecordEnvironmentAsync(wrong, HashB);
        var state = RevisionId.New();
        await RecordStateAsync(state, HashB, required);

        var result = await _authority.CommitAsync(
            _manager.Subject,
            new CommitSharedWorldCommand(
                _world.WorldId,
                reservation.SessionId,
                reservation.Generation,
                "device-a",
                reservation.StartingHead,
                state,
                wrong),
            Now.AddMinutes(1),
            Options);

        Assert.Equal(CommitSharedWorldStatus.InvalidCandidate, result.Status);
        Assert.Equal(reservation.StartingHead, result.CurrentHead);
        Assert.NotNull(await _authority.GetReservationAsync(
            _manager.Subject,
            _world.WorldId,
            Now.AddMinutes(1),
            Options));
    }

    [Fact]
    public async Task SameStateContentResolvesReservationAsUnchanged()
    {
        await RecordStateAsync(_world.CurrentStateRevisionId, HashA, requiredEnvironment: null);
        var acquired = await AcquireManagerAsync();
        var reservation = Assert.IsType<SharedWorldReservation>(acquired.Reservation);
        var candidate = RevisionId.New();
        await RecordStateAsync(candidate, HashA, requiredEnvironment: null);

        var result = await _authority.CommitAsync(
            _manager.Subject,
            new CommitSharedWorldCommand(
                _world.WorldId,
                reservation.SessionId,
                reservation.Generation,
                "device-a",
                reservation.StartingHead,
                candidate,
                null),
            Now.AddMinutes(1),
            Options);

        Assert.Equal(CommitSharedWorldStatus.Unchanged, result.Status);
        Assert.Equal(_world.CurrentStateRevisionId, result.CurrentHead.StateRevisionId);
        Assert.Null(await _authority.GetReservationAsync(
            _manager.Subject,
            _world.WorldId,
            Now.AddMinutes(1),
            Options));
    }

    [Fact]
    public async Task LateInvalidatedGenerationCannotCommitAfterReclaim()
    {
        var member = Steam("76561198000000002");
        await AddActiveMemberAsync(member.Subject);
        var acquired = await AcquireManagerAsync();
        var old = Assert.IsType<SharedWorldReservation>(acquired.Reservation);
        var candidate = RevisionId.New();
        await RecordStateAsync(candidate, HashB, requiredEnvironment: null);
        var uncertainAt = Now + Options.UncertaintyAfter;
        Assert.NotNull(await _authority.GetReservationAsync(
            member.Subject,
            _world.WorldId,
            uncertainAt,
            Options));
        Assert.Equal(
            ReclaimSharedWorldReservationStatus.Reclaimed,
            (await _authority.ReclaimAsync(
                member.Subject,
                _world.WorldId,
                old.SessionId,
                old.Generation,
                uncertainAt + Options.ReclaimAfterUncertain,
                Options)).Status);

        var late = await _authority.CommitAsync(
            _manager.Subject,
            new CommitSharedWorldCommand(
                _world.WorldId,
                old.SessionId,
                old.Generation,
                "device-a",
                old.StartingHead,
                candidate,
                null),
            uncertainAt + Options.ReclaimAfterUncertain + TimeSpan.FromSeconds(1),
            Options);

        Assert.Equal(CommitSharedWorldStatus.ReservationMismatch, late.Status);
        Assert.Equal(old.StartingHead, late.CurrentHead);
        var persisted = Assert.IsType<SharedWorldMetadata>(
            await _worlds.GetAccessibleWorldAsync(member, _world.WorldId));
        Assert.Equal(old.StartingHead.StateRevisionId, persisted.CurrentStateRevisionId);
    }

    private async Task<AcquireSharedWorldReservationResult> AcquireManagerAsync()
        => await _authority.AcquireAsync(
            _manager.Subject,
            _world.WorldId,
            "device-a",
            Head(_world),
            Now,
            Options);

    private async Task AddActiveMemberAsync(ExternalIdentityRef identity)
    {
        const string sql = """
            INSERT INTO steward_world_members (
                world_id,
                provider,
                external_id,
                status,
                added_at)
            VALUES (@world_id, @provider, @external_id, @status, @added_at);
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", _world.WorldId.Value);
        command.Parameters.AddWithValue("provider", identity.Provider);
        command.Parameters.AddWithValue("external_id", identity.ExternalId);
        command.Parameters.AddWithValue("status", (short)SharedWorldMemberStatus.Active);
        command.Parameters.AddWithValue("added_at", Now);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetMemberStatusAsync(
        ExternalIdentityRef identity,
        SharedWorldMemberStatus status)
    {
        const string sql = """
            UPDATE steward_world_members
            SET status = @status
            WHERE world_id = @world_id
              AND provider = @provider
              AND external_id = @external_id;
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("status", (short)status);
        command.Parameters.AddWithValue("world_id", _world.WorldId.Value);
        command.Parameters.AddWithValue("provider", identity.Provider);
        command.Parameters.AddWithValue("external_id", identity.ExternalId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task RecordStateAsync(
        RevisionId revisionId,
        string sha256,
        RevisionId? requiredEnvironment)
    {
        var status = await _revisions.RecordVerifiedStateRevisionAsync(
            new SharedStateRevisionMetadata(
                _world.WorldId,
                revisionId,
                _world.AdapterId,
                $"packages/{_world.WorldId}/state/{revisionId}.package",
                4096,
                sha256,
                requiredEnvironment,
                _manager.Subject,
                Now));
        Assert.True(status is RecordRevisionMetadataStatus.Recorded or RecordRevisionMetadataStatus.AlreadyRecorded);
    }

    private async Task RecordEnvironmentAsync(RevisionId revisionId, string sha256)
    {
        var status = await _revisions.RecordVerifiedEnvironmentRevisionAsync(
            new SharedEnvironmentRevisionMetadata(
                _world.WorldId,
                revisionId,
                _world.AdapterId,
                $"packages/{_world.WorldId}/environment/{revisionId}.package",
                2048,
                sha256,
                _manager.Subject,
                Now));
        Assert.True(status is RecordRevisionMetadataStatus.Recorded or RecordRevisionMetadataStatus.AlreadyRecorded);
    }

    private async Task ResetAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "TRUNCATE TABLE steward_world_reservations, steward_package_transfers, " +
            "steward_world_invitations, steward_state_revisions, steward_environment_revisions, " +
            "steward_world_members, steward_shared_worlds CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private static SharedWorldHead Head(SharedWorldMetadata world)
        => new(world.CurrentStateRevisionId, world.CurrentEnvironmentRevisionId);

    private static VerifiedExternalIdentity Steam(string id)
        => new(new ExternalIdentityRef("steam", id));
}
