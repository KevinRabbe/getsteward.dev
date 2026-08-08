using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.PostgreSql;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;
using Xunit;

namespace SharedWorlds.Backend.PostgreSql.Tests;

public sealed class PostgreSqlLegacySharedWorldAuthorityRetirementTests : IAsyncLifetime
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 8, 8, 2, 0, 0, TimeSpan.Zero);
    private static readonly SharedWorldAuthorityOptions Options =
        SharedWorldAuthorityOptions.FirstReleaseDefaults;

    private NpgsqlDataSource _dataSource = null!;
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

        var worldStore = new PostgreSqlSharedWorldStore(_dataSource);
        var worlds = new SharedWorldMetadataService(worldStore, () => StartedAt);
        _authority = new PostgreSqlSharedWorldAuthorityStore(_dataSource);
        _retirement = new PostgreSqlLegacySharedWorldAuthorityRetirementStore(_dataSource);
        _holder = new VerifiedExternalIdentity(
            new ExternalIdentityRef("steam", "76561198999990401"),
            "Retirement Holder");

        var created = await worlds.CreateSharedWorldAsync(
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
        Assert.Equal(StartedAt.AddSeconds(1), retired.RetiredAt);
        Assert.Null(await _authority.GetReservationAsync(
            _holder.Subject,
            _world.WorldId,
            StartedAt.AddSeconds(2),
            Options));

        var retry = await _retirement.RetireAsync(
            _holder.Subject,
            _world.WorldId,
            "retirement-device",
            reservation.SessionId,
            reservation.Generation,
            StartedAt.AddSeconds(3),
            Options);
        Assert.Equal(LegacySharedWorldAuthorityRetirementStatus.AlreadyRetired, retry.Status);
        Assert.Equal(retired.RetiredAt, retry.RetiredAt);
    }

    [Fact]
    public async Task FutureLegacyAcquireIsRejectedByDatabaseAndGenerationRollsBack()
    {
        var reservation = await AcquireHolderAsync();
        var retired = await _retirement.RetireAsync(
            _holder.Subject,
            _world.WorldId,
            "retirement-device",
            reservation.SessionId,
            reservation.Generation,
            StartedAt.AddSeconds(1),
            Options);
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
        Assert.Contains("legacy writable authority is permanently retired", exception.MessageText);

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
            new ExternalIdentityRef("steam", "76561198999990402"),
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
