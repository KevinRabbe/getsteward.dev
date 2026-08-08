using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlLegacySharedWorldAuthorityRetirementTests : IAsyncLifetime
{
    private const string CandidateHash =
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 8, 2, 0, 0, TimeSpan.Zero);
    private static readonly SharedWorldAuthorityOptions Options =
        SharedWorldAuthorityOptions.FirstReleaseDefaults;

    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlSharedWorldStore _worldStore = null!;
    private SharedWorldMetadataService _worlds = null!;
    private SharedRevisionMetadataService _revisions = null!;
    private PostgreSqlSharedWorldAuthorityStore _authority = null!;
    private PostgreSqlLegacySharedWorldAuthorityRetirementStore _retirement = null!;
    private VerifiedExternalIdentity _holder = null!;
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
        await PostgreSqlLegacySharedWorldAuthorityRetirementSchema.InitializeAsync(_dataSource);
        await ResetAsync();

        _worldStore = new PostgreSqlSharedWorldStore(_dataSource);
        _worlds = new SharedWorldMetadataService(_worldStore, () => StartedAt);
        _revisions = new SharedRevisionMetadataService(_worldStore, _worldStore);
        _authority = new PostgreSqlSharedWorldAuthorityStore(_dataSource);
        _retirement = new PostgreSqlLegacySharedWorldAuthorityRetirementStore(_dataSource);
        _holder = new VerifiedExternalIdentity(
            new ExternalIdentityRef("steam", "76561198999990401"),
            "Retirement Holder");

        var created = await _worlds.CreateSharedWorldAsync(
            _holder,
            new CreateSharedWorldCommand(
                WorldId.New(),
                "factorio",
                "Retirement World",
                RevisionId.New(),
                CurrentEnvironmentRevisionId: null));
        _world = Assert.IsType<SharedWorldMetadata>(created.World);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task ExactLiveReservationRetiresOnceAndDeletesLegacyWriter()
    {
        var reservation = await AcquireHolderAsync();
        var expectedMembership = StableIdentitySetFingerprint.Compute(
        [
            (_holder.Subject.Provider, _holder.Subject.ExternalId)
        ]);

        var retired = await _retirement.RetireAsync(
            _holder.Subject,
            _world.WorldId,
            "retirement-device",
            reservation.SessionId,
            reservation.Generation,
            StartedAt.AddSeconds(1),
            Options);

        Assert.Equal(LegacySharedWorldAuthorityRetirementStatus.Retired, retired.Status);
        Assert.Equal(_holder.Subject, retired.RetiredHolder);
        Assert.Equal("retirement-device", retired.RetiredInstallationId);
        Assert.Equal(reservation.SessionId, retired.RetiredSessionId);
        Assert.Equal(reservation.Generation, retired.RetiredGeneration);
        Assert.Equal(_world.CurrentStateRevisionId, retired.RetiredStateRevisionId);
        Assert.Equal(_world.CurrentEnvironmentRevisionId, retired.RetiredEnvironmentRevisionId);
        Assert.Equal(expectedMembership, retired.RetiredActiveMembersFingerprint);
        Assert.Equal(StartedAt.AddSeconds(1), retired.RetiredAt);
        Assert.Null(await _authority.GetReservationAsync(
            _holder.Subject,
            _world.WorldId,
            StartedAt.AddSeconds(2),
            Options));

        var readBack = await _retirement.GetAsync(
            _holder.Subject,
            _world.WorldId);
        Assert.NotNull(readBack);
        Assert.Equal(LegacySharedWorldAuthorityRetirementStatus.AlreadyRetired, readBack.Status);
        Assert.Equal(retired.RetiredHolder, readBack.RetiredHolder);
        Assert.Equal(retired.RetiredInstallationId, readBack.RetiredInstallationId);
        Assert.Equal(retired.RetiredSessionId, readBack.RetiredSessionId);
        Assert.Equal(retired.RetiredGeneration, readBack.RetiredGeneration);
        Assert.Equal(retired.RetiredStateRevisionId, readBack.RetiredStateRevisionId);
        Assert.Equal(retired.RetiredEnvironmentRevisionId, readBack.RetiredEnvironmentRevisionId);
        Assert.Equal(retired.RetiredActiveMembersFingerprint, readBack.RetiredActiveMembersFingerprint);
        Assert.Equal(retired.RetiredAt, readBack.RetiredAt);

        var other = new ExternalIdentityRef("steam", "76561198999990499");
        Assert.Null(await _retirement.GetAsync(other, _world.WorldId));

        var retry = await _retirement.RetireAsync(
            _holder.Subject,
            _world.WorldId,
            "retirement-device",
            reservation.SessionId,
            reservation.Generation,
            StartedAt.AddSeconds(3),
            Options);
        Assert.Equal(LegacySharedWorldAuthorityRetirementStatus.AlreadyRetired, retry.Status);
        Assert.Equal(readBack, retry);
    }

    [Fact]
    public async Task OrdinaryActiveMemberCannotRetireEvenWhileHoldingLegacyHostReservation()
    {
        var member = new VerifiedExternalIdentity(
            new ExternalIdentityRef("steam", "76561198999990402"),
            "Member Host");
        await AddActiveMemberAsync(member.Subject);
        var acquired = await _authority.AcquireAsync(
            member.Subject,
            _world.WorldId,
            "member-device",
            Head(),
            StartedAt,
            Options);
        Assert.Equal(AcquireSharedWorldReservationStatus.Acquired, acquired.Status);
        var reservation = Assert.IsType<SharedWorldReservation>(acquired.Reservation);

        var result = await _retirement.RetireAsync(
            member.Subject,
            _world.WorldId,
            "member-device",
            reservation.SessionId,
            reservation.Generation,
            StartedAt.AddSeconds(1),
            Options);

        Assert.Equal(
            LegacySharedWorldAuthorityRetirementStatus.NotFoundOrUnauthorized,
            result.Status);
        Assert.Null(await _retirement.GetAsync(member.Subject, _world.WorldId));
        var stillHeld = await _authority.GetReservationAsync(
            member.Subject,
            _world.WorldId,
            StartedAt.AddSeconds(2),
            Options);
        Assert.NotNull(stillHeld);
        Assert.Equal(reservation.SessionId, stillHeld.SessionId);
    }

    [Fact]
    public async Task PendingLegacyInvitationBlocksRetirementUntilAccessStateIsResolved()
    {
        await AddPendingInvitationAsync();
        var reservation = await AcquireHolderAsync();

        var result = await _retirement.RetireAsync(
            _holder.Subject,
            _world.WorldId,
            "retirement-device",
            reservation.SessionId,
            reservation.Generation,
            StartedAt.AddSeconds(1),
            Options);

        Assert.Equal(
            LegacySharedWorldAuthorityRetirementStatus.AccessStateNotReady,
            result.Status);
        Assert.Null(await _retirement.GetAsync(_holder.Subject, _world.WorldId));
        Assert.NotNull(await _authority.GetReservationAsync(
            _holder.Subject,
            _world.WorldId,
            StartedAt.AddSeconds(2),
            Options));
    }

    [Fact]
    public async Task FutureLegacyAcquireIsRejectedByDatabaseAndGenerationRollsBack()
    {
        var reservation = await AcquireHolderAsync();
        var retired = await RetireHolderAsync(reservation);
        Assert.Equal(LegacySharedWorldAuthorityRetirementStatus.Retired, retired.Status);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            _authority.AcquireAsync(
                _holder.Subject,
                _world.WorldId,
                "retirement-device",
                Head(),
                StartedAt.AddSeconds(2),
                Options));
        Assert.Equal("55000", exception.SqlState);
        Assert.Contains("legacy authority is permanently retired", exception.MessageText);

        await using var generationCommand = _dataSource.CreateCommand(
            "SELECT reservation_generation FROM steward_shared_worlds WHERE world_id = @world_id;");
        generationCommand.Parameters.AddWithValue("world_id", _world.WorldId.Value);
        Assert.Equal(reservation.Generation, Convert.ToInt64(await generationCommand.ExecuteScalarAsync()));

        await using var reservationCommand = _dataSource.CreateCommand(
            "SELECT COUNT(*) FROM steward_world_reservations WHERE world_id = @world_id;");
        reservationCommand.Parameters.AddWithValue("world_id", _world.WorldId.Value);
        Assert.Equal(0, Convert.ToInt64(await reservationCommand.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task RetirementAlsoFreezesLegacyWorldAndMembershipMutations()
    {
        var reservation = await AcquireHolderAsync();
        Assert.Equal(
            LegacySharedWorldAuthorityRetirementStatus.Retired,
            (await RetireHolderAsync(reservation)).Status);

        var worldUpdate = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = _dataSource.CreateCommand(
                "UPDATE steward_shared_worlds SET updated_at = @updated_at WHERE world_id = @world_id;");
            command.Parameters.AddWithValue("updated_at", StartedAt.AddMinutes(1));
            command.Parameters.AddWithValue("world_id", _world.WorldId.Value);
            await command.ExecuteNonQueryAsync();
        });
        Assert.Equal("55000", worldUpdate.SqlState);

        var memberInsert = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = _dataSource.CreateCommand(
                """
                INSERT INTO steward_world_members (
                    world_id, provider, external_id, status, added_at)
                VALUES (
                    @world_id, @provider, @external_id, @status, @added_at);
                """);
            command.Parameters.AddWithValue("world_id", _world.WorldId.Value);
            command.Parameters.AddWithValue("provider", "steam");
            command.Parameters.AddWithValue("external_id", "76561198999990488");
            command.Parameters.AddWithValue("status", (short)SharedWorldMemberStatus.Active);
            command.Parameters.AddWithValue("added_at", StartedAt.AddMinutes(1));
            await command.ExecuteNonQueryAsync();
        });
        Assert.Equal("55000", memberInsert.SqlState);
    }

    [Fact]
    public async Task StaleOrWrongReservationCannotCreateRetirementTombstone()
    {
        var reservation = await AcquireHolderAsync();

        var wrongGeneration = await _retirement.RetireAsync(
            _holder.Subject,
            _world.WorldId,
            "retirement-device",
            reservation.SessionId,
            reservation.Generation + 1,
            StartedAt.AddSeconds(1),
            Options);
        Assert.Equal(
            LegacySharedWorldAuthorityRetirementStatus.ReservationMismatch,
            wrongGeneration.Status);

        var stale = await _retirement.RetireAsync(
            _holder.Subject,
            _world.WorldId,
            "retirement-device",
            reservation.SessionId,
            reservation.Generation,
            StartedAt + Options.UncertaintyAfter + TimeSpan.FromSeconds(1),
            Options);
        Assert.Equal(
            LegacySharedWorldAuthorityRetirementStatus.ReservationMismatch,
            stale.Status);

        await using var command = _dataSource.CreateCommand(
            "SELECT COUNT(*) FROM steward_legacy_authority_retirements WHERE world_id = @world_id;");
        command.Parameters.AddWithValue("world_id", _world.WorldId.Value);
        Assert.Equal(0, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task ConcurrentForeignAcquireCannotSurviveSuccessfulRetirement()
    {
        var reservation = await AcquireHolderAsync();
        var other = new VerifiedExternalIdentity(
            new ExternalIdentityRef("steam", "76561198999990403"),
            "Other Member");
        await AddActiveMemberAsync(other.Subject);

        var retirementTask = _retirement.RetireAsync(
            _holder.Subject,
            _world.WorldId,
            "retirement-device",
            reservation.SessionId,
            reservation.Generation,
            StartedAt.AddSeconds(1),
            Options);
        var acquireTask = CaptureAcquireAsync(other.Subject);

        var retired = await retirementTask;
        var foreignAcquire = await acquireTask;

        Assert.Equal(LegacySharedWorldAuthorityRetirementStatus.Retired, retired.Status);
        if (foreignAcquire.Exception is not null)
        {
            var postgres = Assert.IsType<PostgresException>(foreignAcquire.Exception);
            Assert.Equal("55000", postgres.SqlState);
        }
        else
        {
            var result = Assert.IsType<AcquireSharedWorldReservationResult>(foreignAcquire.Result);
            Assert.Equal(AcquireSharedWorldReservationStatus.WorldBusy, result.Status);
        }

        await using var reservationCommand = _dataSource.CreateCommand(
            "SELECT COUNT(*) FROM steward_world_reservations WHERE world_id = @world_id;");
        reservationCommand.Parameters.AddWithValue("world_id", _world.WorldId.Value);
        Assert.Equal(0, Convert.ToInt64(await reservationCommand.ExecuteScalarAsync()));

        await using var retirementCommand = _dataSource.CreateCommand(
            "SELECT COUNT(*) FROM steward_legacy_authority_retirements WHERE world_id = @world_id;");
        retirementCommand.Parameters.AddWithValue("world_id", _world.WorldId.Value);
        Assert.Equal(1, Convert.ToInt64(await retirementCommand.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task ConcurrentCommitAndRetirementCannotBothWinDifferentCanonicalHeads()
    {
        var reservation = await AcquireHolderAsync();
        var originalHead = reservation.StartingHead;
        var candidate = RevisionId.New();
        await RecordStateAsync(candidate);

        var commitTask = _authority.CommitAsync(
            _holder.Subject,
            new CommitSharedWorldCommand(
                _world.WorldId,
                reservation.SessionId,
                reservation.Generation,
                "retirement-device",
                originalHead,
                candidate,
                CandidateEnvironmentRevisionId: null),
            StartedAt.AddSeconds(1),
            Options);
        var retirementTask = _retirement.RetireAsync(
            _holder.Subject,
            _world.WorldId,
            "retirement-device",
            reservation.SessionId,
            reservation.Generation,
            StartedAt.AddSeconds(1),
            Options);

        var commit = await commitTask;
        var retirement = await retirementTask;
        var persisted = Assert.IsType<SharedWorldMetadata>(
            await _worlds.GetAccessibleWorldAsync(_holder, _world.WorldId));

        if (retirement.Status == LegacySharedWorldAuthorityRetirementStatus.Retired)
        {
            Assert.Equal(CommitSharedWorldStatus.ReservationMismatch, commit.Status);
            Assert.Equal(originalHead.StateRevisionId, persisted.CurrentStateRevisionId);
            Assert.Equal(originalHead.EnvironmentRevisionId, persisted.CurrentEnvironmentRevisionId);
            Assert.Equal(originalHead.StateRevisionId, retirement.RetiredStateRevisionId);
            Assert.Equal(originalHead.EnvironmentRevisionId, retirement.RetiredEnvironmentRevisionId);
        }
        else
        {
            Assert.Equal(LegacySharedWorldAuthorityRetirementStatus.ReservationMismatch, retirement.Status);
            Assert.Equal(CommitSharedWorldStatus.Committed, commit.Status);
            Assert.Equal(candidate, persisted.CurrentStateRevisionId);
            Assert.Null(persisted.CurrentEnvironmentRevisionId);
            Assert.Null(await _retirement.GetAsync(_holder.Subject, _world.WorldId));
        }

        Assert.False(
            retirement.Status == LegacySharedWorldAuthorityRetirementStatus.Retired &&
            commit.Status == CommitSharedWorldStatus.Committed);
    }

    private async Task<LegacySharedWorldAuthorityRetirementResult> RetireHolderAsync(
        SharedWorldReservation reservation)
        => await _retirement.RetireAsync(
            _holder.Subject,
            _world.WorldId,
            "retirement-device",
            reservation.SessionId,
            reservation.Generation,
            StartedAt.AddSeconds(1),
            Options);

    private async Task<SharedWorldReservation> AcquireHolderAsync()
    {
        var acquired = await _authority.AcquireAsync(
            _holder.Subject,
            _world.WorldId,
            "retirement-device",
            Head(),
            StartedAt,
            Options);
        Assert.Equal(AcquireSharedWorldReservationStatus.Acquired, acquired.Status);
        return Assert.IsType<SharedWorldReservation>(acquired.Reservation);
    }

    private SharedWorldHead Head()
        => new(
            _world.CurrentStateRevisionId,
            _world.CurrentEnvironmentRevisionId);

    private async Task RecordStateAsync(RevisionId revisionId)
    {
        var status = await _revisions.RecordVerifiedStateRevisionAsync(
            new SharedStateRevisionMetadata(
                _world.WorldId,
                revisionId,
                _world.AdapterId,
                $"packages/{_world.WorldId}/state/{revisionId}.package",
                4096,
                CandidateHash,
                RequiredEnvironmentRevisionId: null,
                _holder.Subject,
                StartedAt));
        Assert.True(
            status is RecordRevisionMetadataStatus.Recorded or
            RecordRevisionMetadataStatus.AlreadyRecorded);
    }

    private async Task AddActiveMemberAsync(ExternalIdentityRef member)
    {
        const string sql = """
            INSERT INTO steward_world_members (
                world_id,
                provider,
                external_id,
                status,
                added_at)
            VALUES (
                @world_id,
                @provider,
                @external_id,
                @status,
                @added_at);
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", _world.WorldId.Value);
        command.Parameters.AddWithValue("provider", member.Provider);
        command.Parameters.AddWithValue("external_id", member.ExternalId);
        command.Parameters.AddWithValue("status", (short)SharedWorldMemberStatus.Active);
        command.Parameters.AddWithValue("added_at", StartedAt);
        await command.ExecuteNonQueryAsync();
    }

    private async Task AddPendingInvitationAsync()
    {
        const string sql = """
            INSERT INTO steward_world_invitations (
                invitation_id,
                world_id,
                invited_provider,
                invited_external_id,
                invited_by_provider,
                invited_by_external_id,
                status,
                created_at,
                responded_at)
            VALUES (
                @invitation_id,
                @world_id,
                @invited_provider,
                @invited_external_id,
                @invited_by_provider,
                @invited_by_external_id,
                @status,
                @created_at,
                NULL);
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("invitation_id", Guid.NewGuid());
        command.Parameters.AddWithValue("world_id", _world.WorldId.Value);
        command.Parameters.AddWithValue("invited_provider", "steam");
        command.Parameters.AddWithValue("invited_external_id", "76561198999990477");
        command.Parameters.AddWithValue("invited_by_provider", _holder.Subject.Provider);
        command.Parameters.AddWithValue("invited_by_external_id", _holder.Subject.ExternalId);
        command.Parameters.AddWithValue("status", (short)WorldAccessInvitationStatus.Pending);
        command.Parameters.AddWithValue("created_at", StartedAt);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<AcquireCapture> CaptureAcquireAsync(ExternalIdentityRef caller)
    {
        try
        {
            var result = await _authority.AcquireAsync(
                caller,
                _world.WorldId,
                "foreign-device",
                Head(),
                StartedAt.AddSeconds(1),
                Options);
            return new AcquireCapture(result, null);
        }
        catch (Exception exception)
        {
            return new AcquireCapture(null, exception);
        }
    }

    private async Task ResetAsync()
    {
        await using var command = _dataSource.CreateCommand(
            "TRUNCATE TABLE steward_legacy_authority_retirements, " +
            "steward_world_reservations, steward_package_transfers, steward_world_invitations, " +
            "steward_state_revisions, steward_environment_revisions, steward_world_members, " +
            "steward_shared_worlds CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private sealed record AcquireCapture(
        AcquireSharedWorldReservationResult? Result,
        Exception? Exception);
}
