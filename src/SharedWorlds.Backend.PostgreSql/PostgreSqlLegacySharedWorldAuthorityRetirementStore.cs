using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.PostgreSql;

/// <summary>
/// One-way PostgreSQL cutover from the legacy shared-World reservation writer. The transaction locks
/// the canonical World row first, matching legacy acquire/commit lock order, then proves the exact
/// still-live reservation, persists the retirement tombstone, and removes that reservation before
/// commit. The database trigger prevents any later reservation INSERT/UPDATE for the World.
/// </summary>
public sealed class PostgreSqlLegacySharedWorldAuthorityRetirementStore :
    ILegacySharedWorldAuthorityRetirementStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlLegacySharedWorldAuthorityRetirementStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<LegacySharedWorldAuthorityRetirementResult> RetireAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        string installationId,
        Guid sessionId,
        long generation,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default)
    {
        Validate(worldId, installationId, sessionId, generation, options);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (!await LockWorldAsync(connection, transaction, worldId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(
                LegacySharedWorldAuthorityRetirementStatus.NotFoundOrUnauthorized,
                worldId);
        }

        var existing = await LoadRetirementAsync(
            connection,
            transaction,
            worldId,
            cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            if (existing.Holder != caller)
            {
                return Result(
                    LegacySharedWorldAuthorityRetirementStatus.NotFoundOrUnauthorized,
                    worldId);
            }

            return existing.InstallationId == installationId &&
                   existing.SessionId == sessionId &&
                   existing.Generation == generation
                ? Result(
                    LegacySharedWorldAuthorityRetirementStatus.AlreadyRetired,
                    worldId,
                    existing)
                : Result(
                    LegacySharedWorldAuthorityRetirementStatus.ReservationMismatch,
                    worldId,
                    existing);
        }

        var reservation = await LoadReservationForUpdateAsync(
            connection,
            transaction,
            worldId,
            cancellationToken);
        if (reservation is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return Result(
                LegacySharedWorldAuthorityRetirementStatus.ReservationMismatch,
                worldId);
        }

        if (reservation.Holder != caller)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(
                LegacySharedWorldAuthorityRetirementStatus.NotFoundOrUnauthorized,
                worldId);
        }

        var reservationIsCurrent =
            reservation.InstallationId == installationId &&
            reservation.SessionId == sessionId &&
            reservation.Generation == generation &&
            reservation.State == SharedWorldReservationState.Active &&
            reservation.LastHeartbeatAt > serverNow - options.UncertaintyAfter;
        if (!reservationIsCurrent)
        {
            await transaction.CommitAsync(cancellationToken);
            return Result(
                LegacySharedWorldAuthorityRetirementStatus.ReservationMismatch,
                worldId);
        }

        var retirement = new RetirementRow(
            caller,
            installationId,
            sessionId,
            generation,
            serverNow);
        await InsertRetirementAsync(
            connection,
            transaction,
            worldId,
            retirement,
            cancellationToken);
        await DeleteExactReservationAsync(
            connection,
            transaction,
            worldId,
            retirement,
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return Result(
            LegacySharedWorldAuthorityRetirementStatus.Retired,
            worldId,
            retirement);
    }

    private static async Task<bool> LockWorldAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1
            FROM steward_shared_worlds
            WHERE world_id = @world_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<RetirementRow?> LoadRetirementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                holder_provider,
                holder_external_id,
                installation_id,
                session_id,
                generation,
                retired_at
            FROM steward_legacy_authority_retirements
            WHERE world_id = @world_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RetirementRow(
            new ExternalIdentityRef(
                reader.GetString(reader.GetOrdinal("holder_provider")),
                reader.GetString(reader.GetOrdinal("holder_external_id"))),
            reader.GetString(reader.GetOrdinal("installation_id")),
            reader.GetGuid(reader.GetOrdinal("session_id")),
            reader.GetInt64(reader.GetOrdinal("generation")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("retired_at")));
    }

    private static async Task<ReservationRow?> LoadReservationForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                session_id,
                generation,
                holder_provider,
                holder_external_id,
                installation_id,
                state,
                last_heartbeat_at
            FROM steward_world_reservations
            WHERE world_id = @world_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ReservationRow(
            reader.GetGuid(reader.GetOrdinal("session_id")),
            reader.GetInt64(reader.GetOrdinal("generation")),
            new ExternalIdentityRef(
                reader.GetString(reader.GetOrdinal("holder_provider")),
                reader.GetString(reader.GetOrdinal("holder_external_id"))),
            reader.GetString(reader.GetOrdinal("installation_id")),
            (SharedWorldReservationState)reader.GetInt16(reader.GetOrdinal("state")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_heartbeat_at")));
    }

    private static async Task InsertRetirementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        RetirementRow retirement,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO steward_legacy_authority_retirements (
                world_id,
                holder_provider,
                holder_external_id,
                installation_id,
                session_id,
                generation,
                retired_at)
            VALUES (
                @world_id,
                @holder_provider,
                @holder_external_id,
                @installation_id,
                @session_id,
                @generation,
                @retired_at);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("holder_provider", retirement.Holder.Provider);
        command.Parameters.AddWithValue("holder_external_id", retirement.Holder.ExternalId);
        command.Parameters.AddWithValue("installation_id", retirement.InstallationId);
        command.Parameters.AddWithValue("session_id", retirement.SessionId);
        command.Parameters.AddWithValue("generation", retirement.Generation);
        command.Parameters.AddWithValue("retired_at", retirement.RetiredAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteExactReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        RetirementRow retirement,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM steward_world_reservations
            WHERE world_id = @world_id
              AND session_id = @session_id
              AND generation = @generation
              AND holder_provider = @holder_provider
              AND holder_external_id = @holder_external_id
              AND installation_id = @installation_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("session_id", retirement.SessionId);
        command.Parameters.AddWithValue("generation", retirement.Generation);
        command.Parameters.AddWithValue("holder_provider", retirement.Holder.Provider);
        command.Parameters.AddWithValue("holder_external_id", retirement.Holder.ExternalId);
        command.Parameters.AddWithValue("installation_id", retirement.InstallationId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "Legacy reservation changed while the World retirement transaction held its row lock.");
        }
    }

    private static LegacySharedWorldAuthorityRetirementResult Result(
        LegacySharedWorldAuthorityRetirementStatus status,
        WorldId worldId,
        RetirementRow? retirement = null)
        => new(
            status,
            worldId,
            retirement?.Holder,
            retirement?.InstallationId,
            retirement?.SessionId,
            retirement?.Generation,
            retirement?.RetiredAt);

    private static void Validate(
        WorldId worldId,
        string installationId,
        Guid sessionId,
        long generation,
        SharedWorldAuthorityOptions options)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }

        if (string.IsNullOrWhiteSpace(installationId) ||
            installationId.Length > 128 ||
            installationId.Any(char.IsControl))
        {
            throw new ArgumentException("A bounded installation ID is required.", nameof(installationId));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Reservation session ID is required.", nameof(sessionId));
        }

        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        ArgumentNullException.ThrowIfNull(options);
    }

    private sealed record ReservationRow(
        Guid SessionId,
        long Generation,
        ExternalIdentityRef Holder,
        string InstallationId,
        SharedWorldReservationState State,
        DateTimeOffset LastHeartbeatAt);

    private sealed record RetirementRow(
        ExternalIdentityRef Holder,
        string InstallationId,
        Guid SessionId,
        long Generation,
        DateTimeOffset RetiredAt);
}
