using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.PostgreSql;

/// <summary>
/// Completes BE-D013 idempotency for World reservation mutations. Acquire/reclaim execute and persist
/// their exact result in one PostgreSQL transaction; commit delegates to the already-proven atomic
/// commit-idempotency store. Normal read/heartbeat/non-idempotent methods delegate unchanged.
/// </summary>
public sealed class PostgreSqlIdempotentReservationAuthorityStore : ISharedWorldAuthorityStore
{
    private const string AcquireOperation = "world.acquire";
    private const string ReclaimOperation = "world.reclaim";
    private static readonly TimeSpan IdempotencyLifetime = TimeSpan.FromDays(7);

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlIdempotentSharedWorldAuthorityStore _inner;

    public PostgreSqlIdempotentReservationAuthorityStore(
        NpgsqlDataSource dataSource,
        PostgreSqlIdempotentSharedWorldAuthorityStore inner)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(inner);
        _dataSource = dataSource;
        _inner = inner;
    }

    public Task<AcquireSharedWorldReservationResult> AcquireAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        string installationId,
        SharedWorldHead expectedHead,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default)
        => _inner.AcquireAsync(
            caller,
            worldId,
            installationId,
            expectedHead,
            serverNow,
            options,
            cancellationToken);

    public Task<SharedWorldReservation?> GetReservationAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default)
        => _inner.GetReservationAsync(caller, worldId, serverNow, options, cancellationToken);

    public Task<SharedWorldHeartbeatStatus> HeartbeatAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        string installationId,
        Guid sessionId,
        long generation,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default)
        => _inner.HeartbeatAsync(
            caller,
            worldId,
            installationId,
            sessionId,
            generation,
            serverNow,
            options,
            cancellationToken);

    public Task<ReclaimSharedWorldReservationResult> ReclaimAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        Guid expectedSessionId,
        long expectedGeneration,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default)
        => _inner.ReclaimAsync(
            caller,
            worldId,
            expectedSessionId,
            expectedGeneration,
            serverNow,
            options,
            cancellationToken);

    public Task<CommitSharedWorldResult> CommitAsync(
        ExternalIdentityRef caller,
        CommitSharedWorldCommand command,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default)
        => _inner.CommitAsync(caller, command, serverNow, options, cancellationToken);

    public Task<IdempotentMutationResult<CommitSharedWorldResult>> CommitIdempotentAsync(
        ExternalIdentityRef caller,
        CommitSharedWorldCommand command,
        StewardIdempotencyKey idempotencyKey,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default)
        => _inner.CommitIdempotentAsync(
            caller,
            command,
            idempotencyKey,
            serverNow,
            options,
            cancellationToken);

    public Task<bool> HasUnresolvedWritableResponsibilityAsync(
        WorldId worldId,
        ExternalIdentityRef identity,
        CancellationToken cancellationToken = default)
        => _inner.HasUnresolvedWritableResponsibilityAsync(worldId, identity, cancellationToken);

    public async Task<IdempotentMutationResult<AcquireSharedWorldReservationResult>> AcquireIdempotentAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        string installationId,
        SharedWorldHead expectedHead,
        StewardIdempotencyKey idempotencyKey,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(caller);
        ValidateWorldId(worldId);
        ValidateInstallationId(installationId);
        ValidateHead(expectedHead);
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        ArgumentNullException.ThrowIfNull(options);

        var requestHash = HashLogicalFields(
            worldId.Value.ToString("N"),
            installationId,
            expectedHead.StateRevisionId.Value.ToString("N"),
            expectedHead.EnvironmentRevisionId?.Value.ToString("N"));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PrepareIdempotencyAsync(
            connection,
            transaction,
            caller,
            AcquireOperation,
            idempotencyKey,
            serverNow,
            cancellationToken);

        var replay = await TryLoadIdempotentResultAsync<AcquireSharedWorldReservationResult>(
            connection,
            transaction,
            caller,
            AcquireOperation,
            idempotencyKey,
            requestHash,
            serverNow,
            cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay;
        }

        var result = await AcquireCoreAsync(
            connection,
            transaction,
            caller,
            worldId,
            installationId,
            expectedHead,
            serverNow,
            options,
            cancellationToken);
        await SaveIdempotentResultAsync(
            connection,
            transaction,
            caller,
            AcquireOperation,
            idempotencyKey,
            worldId,
            requestHash,
            result,
            serverNow,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(IdempotentMutationStatus.Executed, result);
    }

    public async Task<IdempotentMutationResult<ReclaimSharedWorldReservationResult>> ReclaimIdempotentAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        Guid expectedSessionId,
        long expectedGeneration,
        StewardIdempotencyKey idempotencyKey,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(caller);
        ValidateWorldId(worldId);
        ValidateSession(expectedSessionId, expectedGeneration);
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        ArgumentNullException.ThrowIfNull(options);

        var requestHash = HashLogicalFields(
            worldId.Value.ToString("N"),
            expectedSessionId.ToString("N"),
            expectedGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await PrepareIdempotencyAsync(
            connection,
            transaction,
            caller,
            ReclaimOperation,
            idempotencyKey,
            serverNow,
            cancellationToken);

        var replay = await TryLoadIdempotentResultAsync<ReclaimSharedWorldReservationResult>(
            connection,
            transaction,
            caller,
            ReclaimOperation,
            idempotencyKey,
            requestHash,
            serverNow,
            cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay;
        }

        var result = await ReclaimCoreAsync(
            connection,
            transaction,
            caller,
            worldId,
            expectedSessionId,
            expectedGeneration,
            serverNow,
            options,
            cancellationToken);
        await SaveIdempotentResultAsync(
            connection,
            transaction,
            caller,
            ReclaimOperation,
            idempotencyKey,
            worldId,
            requestHash,
            result,
            serverNow,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(IdempotentMutationStatus.Executed, result);
    }

    private static async Task<AcquireSharedWorldReservationResult> AcquireCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExternalIdentityRef caller,
        WorldId worldId,
        string installationId,
        SharedWorldHead expectedHead,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken)
    {
        var world = await LoadWorldForUpdateAsync(connection, transaction, worldId, cancellationToken);
        if (world is null ||
            !await IsActiveMemberAsync(connection, transaction, worldId, caller, cancellationToken))
        {
            return new(AcquireSharedWorldReservationStatus.NotFoundOrUnauthorized, null, null);
        }

        await RefreshUncertaintyAsync(
            connection,
            transaction,
            worldId,
            serverNow,
            options,
            cancellationToken);

        if (world.Head != expectedHead)
        {
            return new(AcquireSharedWorldReservationStatus.HeadChanged, null, world.Head);
        }

        var existing = await LoadReservationForUpdateAsync(
            connection,
            transaction,
            worldId,
            cancellationToken);
        if (existing is not null)
        {
            var status = existing.State == SharedWorldReservationState.Uncertain
                ? AcquireSharedWorldReservationStatus.WorldUncertain
                : existing.Holder == caller &&
                  string.Equals(existing.InstallationId, installationId, StringComparison.Ordinal)
                    ? AcquireSharedWorldReservationStatus.AlreadyHeldByCaller
                    : AcquireSharedWorldReservationStatus.WorldBusy;
            return new(status, existing, world.Head);
        }

        var generation = await IncrementGenerationAsync(
            connection,
            transaction,
            worldId,
            cancellationToken);
        var reservation = new SharedWorldReservation(
            worldId,
            Guid.NewGuid(),
            generation,
            caller,
            installationId,
            world.Head,
            SharedWorldReservationState.Active,
            serverNow,
            serverNow,
            null);
        await InsertReservationAsync(connection, transaction, reservation, cancellationToken);
        return new(AcquireSharedWorldReservationStatus.Acquired, reservation, world.Head);
    }

    private static async Task<ReclaimSharedWorldReservationResult> ReclaimCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExternalIdentityRef caller,
        WorldId worldId,
        Guid expectedSessionId,
        long expectedGeneration,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken)
    {
        var world = await LoadWorldForUpdateAsync(connection, transaction, worldId, cancellationToken);
        if (world is null ||
            !await IsActiveMemberAsync(connection, transaction, worldId, caller, cancellationToken))
        {
            return new(ReclaimSharedWorldReservationStatus.NotFoundOrUnauthorized, null, null);
        }

        await RefreshUncertaintyAsync(
            connection,
            transaction,
            worldId,
            serverNow,
            options,
            cancellationToken);
        var reservation = await LoadReservationForUpdateAsync(
            connection,
            transaction,
            worldId,
            cancellationToken);

        if (reservation is null ||
            reservation.SessionId != expectedSessionId ||
            reservation.Generation != expectedGeneration ||
            reservation.State != SharedWorldReservationState.Uncertain ||
            reservation.BecameUncertainAt is null)
        {
            return new(ReclaimSharedWorldReservationStatus.ReservationMismatch, null, world.Head);
        }

        if (serverNow - reservation.BecameUncertainAt.Value < options.ReclaimAfterUncertain)
        {
            return new(ReclaimSharedWorldReservationStatus.GracePeriodRequired, null, world.Head);
        }

        await DeleteReservationAsync(
            connection,
            transaction,
            worldId,
            expectedSessionId,
            expectedGeneration,
            cancellationToken);
        return new(ReclaimSharedWorldReservationStatus.Reclaimed, expectedGeneration, world.Head);
    }

    private static async Task PrepareIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExternalIdentityRef caller,
        string operation,
        StewardIdempotencyKey key,
        DateTimeOffset serverNow,
        CancellationToken cancellationToken)
    {
        const string lockSql = "SELECT pg_advisory_xact_lock(@lock_key);";
        await using (var lockCommand = new NpgsqlCommand(lockSql, connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("lock_key", CreateAdvisoryLockKey(caller, operation, key));
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string deleteSql = "DELETE FROM steward_authority_idempotency WHERE expires_at <= @server_now;";
        await using var deleteCommand = new NpgsqlCommand(deleteSql, connection, transaction);
        deleteCommand.Parameters.AddWithValue("server_now", serverNow);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IdempotentMutationResult<T>?> TryLoadIdempotentResultAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExternalIdentityRef caller,
        string operation,
        StewardIdempotencyKey key,
        string requestHash,
        DateTimeOffset serverNow,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT request_sha256, result_json::text, expires_at
            FROM steward_authority_idempotency
            WHERE caller_provider = @caller_provider
              AND caller_external_id = @caller_external_id
              AND operation = @operation
              AND idempotency_key = @idempotency_key;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdempotencyIdentityParameters(command, caller, operation, key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var storedHash = reader.GetString(reader.GetOrdinal("request_sha256"));
        var resultJson = reader.GetString(reader.GetOrdinal("result_json"));
        var expiresAt = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("expires_at"));
        await reader.DisposeAsync();

        if (expiresAt <= serverNow)
        {
            await DeleteIdempotencyKeyAsync(
                connection,
                transaction,
                caller,
                operation,
                key,
                cancellationToken);
            return null;
        }

        if (!string.Equals(storedHash, requestHash, StringComparison.Ordinal))
        {
            return new(IdempotentMutationStatus.KeyConflict, default);
        }

        var result = JsonSerializer.Deserialize<T>(resultJson)
            ?? throw new InvalidOperationException("Stored idempotency result could not be deserialized.");
        return new(IdempotentMutationStatus.Replayed, result);
    }

    private static async Task SaveIdempotentResultAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExternalIdentityRef caller,
        string operation,
        StewardIdempotencyKey key,
        WorldId worldId,
        string requestHash,
        T result,
        DateTimeOffset serverNow,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO steward_authority_idempotency (
                caller_provider,
                caller_external_id,
                operation,
                idempotency_key,
                world_id,
                request_sha256,
                result_json,
                created_at,
                expires_at)
            VALUES (
                @caller_provider,
                @caller_external_id,
                @operation,
                @idempotency_key,
                @world_id,
                @request_sha256,
                @result_json,
                @created_at,
                @expires_at);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdempotencyIdentityParameters(command, caller, operation, key);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("request_sha256", requestHash);
        command.Parameters.AddWithValue("result_json", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(result));
        command.Parameters.AddWithValue("created_at", serverNow);
        command.Parameters.AddWithValue("expires_at", serverNow + IdempotencyLifetime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteIdempotencyKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExternalIdentityRef caller,
        string operation,
        StewardIdempotencyKey key,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM steward_authority_idempotency
            WHERE caller_provider = @caller_provider
              AND caller_external_id = @caller_external_id
              AND operation = @operation
              AND idempotency_key = @idempotency_key;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddIdempotencyIdentityParameters(command, caller, operation, key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddIdempotencyIdentityParameters(
        NpgsqlCommand command,
        ExternalIdentityRef caller,
        string operation,
        StewardIdempotencyKey key)
    {
        command.Parameters.AddWithValue("caller_provider", caller.Provider);
        command.Parameters.AddWithValue("caller_external_id", caller.ExternalId);
        command.Parameters.AddWithValue("operation", operation);
        command.Parameters.AddWithValue("idempotency_key", key.Value);
    }

    private static long CreateAdvisoryLockKey(
        ExternalIdentityRef caller,
        string operation,
        StewardIdempotencyKey key)
    {
        var hash = HashLogicalFieldsBytes(caller.Provider, caller.ExternalId, operation, key.Value);
        return BinaryPrimitives.ReadInt64BigEndian(hash.AsSpan(0, sizeof(long)));
    }

    private static string HashLogicalFields(params string?[] fields)
        => Convert.ToHexString(HashLogicalFieldsBytes(fields));

    private static byte[] HashLogicalFieldsBytes(params string?[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var field in fields)
        {
            if (field is null)
            {
                BinaryPrimitives.WriteInt32BigEndian(length, -1);
                hash.AppendData(length);
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return hash.GetHashAndReset();
    }

    private static async Task<WorldRow?> LoadWorldForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT current_state_revision_id, current_environment_revision_id
            FROM steward_shared_worlds
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

        var environmentOrdinal = reader.GetOrdinal("current_environment_revision_id");
        return new WorldRow(new SharedWorldHead(
            new RevisionId(reader.GetGuid(reader.GetOrdinal("current_state_revision_id"))),
            reader.IsDBNull(environmentOrdinal)
                ? null
                : new RevisionId(reader.GetGuid(environmentOrdinal))));
    }

    private static async Task<bool> IsActiveMemberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        ExternalIdentityRef identity,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT status
            FROM steward_world_members
            WHERE world_id = @world_id
              AND provider = @provider
              AND external_id = @external_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("provider", identity.Provider);
        command.Parameters.AddWithValue("external_id", identity.ExternalId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is short status && status == (short)SharedWorldMemberStatus.Active;
    }

    private static async Task RefreshUncertaintyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE steward_world_reservations
            SET state = @uncertain_state,
                became_uncertain_at = last_heartbeat_at + @uncertainty_after
            WHERE world_id = @world_id
              AND state = @active_state
              AND last_heartbeat_at <= @uncertainty_cutoff;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("uncertain_state", (short)SharedWorldReservationState.Uncertain);
        command.Parameters.AddWithValue("uncertainty_after", NpgsqlDbType.Interval, options.UncertaintyAfter);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("active_state", (short)SharedWorldReservationState.Active);
        command.Parameters.AddWithValue("uncertainty_cutoff", serverNow - options.UncertaintyAfter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<SharedWorldReservation?> LoadReservationForUpdateAsync(
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
                starting_state_revision_id,
                starting_environment_revision_id,
                state,
                acquired_at,
                last_heartbeat_at,
                became_uncertain_at
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

        var environmentOrdinal = reader.GetOrdinal("starting_environment_revision_id");
        var uncertainOrdinal = reader.GetOrdinal("became_uncertain_at");
        return new SharedWorldReservation(
            worldId,
            reader.GetGuid(reader.GetOrdinal("session_id")),
            reader.GetInt64(reader.GetOrdinal("generation")),
            new ExternalIdentityRef(
                reader.GetString(reader.GetOrdinal("holder_provider")),
                reader.GetString(reader.GetOrdinal("holder_external_id"))),
            reader.GetString(reader.GetOrdinal("installation_id")),
            new SharedWorldHead(
                new RevisionId(reader.GetGuid(reader.GetOrdinal("starting_state_revision_id"))),
                reader.IsDBNull(environmentOrdinal)
                    ? null
                    : new RevisionId(reader.GetGuid(environmentOrdinal))),
            (SharedWorldReservationState)reader.GetInt16(reader.GetOrdinal("state")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("acquired_at")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_heartbeat_at")),
            reader.IsDBNull(uncertainOrdinal)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(uncertainOrdinal));
    }

    private static async Task<long> IncrementGenerationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE steward_shared_worlds
            SET reservation_generation = reservation_generation + 1
            WHERE world_id = @world_id
            RETURNING reservation_generation;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task InsertReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SharedWorldReservation reservation,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO steward_world_reservations (
                world_id,
                session_id,
                generation,
                holder_provider,
                holder_external_id,
                installation_id,
                starting_state_revision_id,
                starting_environment_revision_id,
                state,
                acquired_at,
                last_heartbeat_at,
                became_uncertain_at)
            VALUES (
                @world_id,
                @session_id,
                @generation,
                @holder_provider,
                @holder_external_id,
                @installation_id,
                @starting_state_revision_id,
                @starting_environment_revision_id,
                @state,
                @acquired_at,
                @last_heartbeat_at,
                NULL);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", reservation.WorldId.Value);
        command.Parameters.AddWithValue("session_id", reservation.SessionId);
        command.Parameters.AddWithValue("generation", reservation.Generation);
        command.Parameters.AddWithValue("holder_provider", reservation.Holder.Provider);
        command.Parameters.AddWithValue("holder_external_id", reservation.Holder.ExternalId);
        command.Parameters.AddWithValue("installation_id", reservation.InstallationId);
        command.Parameters.AddWithValue("starting_state_revision_id", reservation.StartingHead.StateRevisionId.Value);
        command.Parameters.Add(new NpgsqlParameter("starting_environment_revision_id", NpgsqlDbType.Uuid)
        {
            Value = reservation.StartingHead.EnvironmentRevisionId is { } environment
                ? environment.Value
                : DBNull.Value
        });
        command.Parameters.AddWithValue("state", (short)reservation.State);
        command.Parameters.AddWithValue("acquired_at", reservation.AcquiredAt);
        command.Parameters.AddWithValue("last_heartbeat_at", reservation.LastHeartbeatAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        Guid sessionId,
        long generation,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM steward_world_reservations
            WHERE world_id = @world_id
              AND session_id = @session_id
              AND generation = @generation;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("session_id", sessionId);
        command.Parameters.AddWithValue("generation", generation);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Reservation changed during an idempotent authority transaction.");
        }
    }

    private static void ValidateIdentity(ExternalIdentityRef identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity.ExternalId);
    }

    private static void ValidateWorldId(WorldId worldId)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }
    }

    private static void ValidateInstallationId(string installationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        if (installationId.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(installationId));
        }
    }

    private static void ValidateHead(SharedWorldHead head)
    {
        ArgumentNullException.ThrowIfNull(head);
        if (head.StateRevisionId.Value == Guid.Empty ||
            head.EnvironmentRevisionId is { Value: var environment } && environment == Guid.Empty)
        {
            throw new ArgumentException("World head contains an empty revision ID.", nameof(head));
        }
    }

    private static void ValidateSession(Guid sessionId, long generation)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session ID is required.", nameof(sessionId));
        }

        if (generation <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }
    }

    private sealed record WorldRow(SharedWorldHead Head);
}
