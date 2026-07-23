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
/// Adds durable transaction-level idempotency to authority mutations while delegating the already
/// proven non-idempotent/read paths to PostgreSqlSharedWorldAuthorityStore. Commit is implemented
/// first because an ambiguous successful commit removes its reservation and therefore cannot be
/// reconstructed safely from reservation state alone.
/// </summary>
public sealed class PostgreSqlIdempotentSharedWorldAuthorityStore : ISharedWorldAuthorityStore
{
    private const string CommitOperation = "world.commit";
    private static readonly TimeSpan IdempotencyLifetime = TimeSpan.FromDays(7);

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlSharedWorldAuthorityStore _inner;

    public PostgreSqlIdempotentSharedWorldAuthorityStore(
        NpgsqlDataSource dataSource,
        PostgreSqlSharedWorldAuthorityStore inner)
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

    public Task<bool> HasUnresolvedWritableResponsibilityAsync(
        WorldId worldId,
        ExternalIdentityRef identity,
        CancellationToken cancellationToken = default)
        => _inner.HasUnresolvedWritableResponsibilityAsync(worldId, identity, cancellationToken);

    public async Task<IdempotentMutationResult<CommitSharedWorldResult>> CommitIdempotentAsync(
        ExternalIdentityRef caller,
        CommitSharedWorldCommand command,
        StewardIdempotencyKey idempotencyKey,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(caller);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(idempotencyKey);
        ArgumentNullException.ThrowIfNull(options);
        ValidateCommit(command);

        var requestHash = HashLogicalFields(
            command.WorldId.Value.ToString("N"),
            command.SessionId.ToString("N"),
            command.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            command.InstallationId,
            command.ExpectedHead.StateRevisionId.Value.ToString("N"),
            command.ExpectedHead.EnvironmentRevisionId?.Value.ToString("N"),
            command.CandidateStateRevisionId.Value.ToString("N"),
            command.CandidateEnvironmentRevisionId?.Value.ToString("N"));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AcquireIdempotencyLockAsync(
            connection,
            transaction,
            caller,
            CommitOperation,
            idempotencyKey,
            cancellationToken);
        await DeleteExpiredIdempotencyAsync(connection, transaction, serverNow, cancellationToken);

        var replay = await TryLoadIdempotentResultAsync<CommitSharedWorldResult>(
            connection,
            transaction,
            caller,
            CommitOperation,
            idempotencyKey,
            requestHash,
            serverNow,
            cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay;
        }

        var result = await CommitCoreAsync(
            connection,
            transaction,
            caller,
            command,
            serverNow,
            cancellationToken);
        await SaveIdempotentResultAsync(
            connection,
            transaction,
            caller,
            CommitOperation,
            idempotencyKey,
            command.WorldId,
            requestHash,
            result,
            serverNow,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(IdempotentMutationStatus.Executed, result);
    }

    private static async Task<CommitSharedWorldResult> CommitCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExternalIdentityRef caller,
        CommitSharedWorldCommand command,
        DateTimeOffset serverNow,
        CancellationToken cancellationToken)
    {
        var world = await LoadWorldForUpdateAsync(
            connection,
            transaction,
            command.WorldId,
            cancellationToken);
        if (world is null)
        {
            throw new InvalidOperationException("Reservation references a World that no longer exists.");
        }

        var reservation = await LoadReservationForUpdateAsync(
            connection,
            transaction,
            command.WorldId,
            cancellationToken);
        if (reservation is null ||
            reservation.SessionId != command.SessionId ||
            reservation.Generation != command.Generation ||
            reservation.Holder != caller ||
            !string.Equals(reservation.InstallationId, command.InstallationId, StringComparison.Ordinal))
        {
            return new(CommitSharedWorldStatus.ReservationMismatch, world.Head, null);
        }

        if (world.Head != command.ExpectedHead || reservation.StartingHead != command.ExpectedHead)
        {
            return new(CommitSharedWorldStatus.HeadChanged, world.Head, null);
        }

        var candidateState = await LoadStateCandidateAsync(
            connection,
            transaction,
            command.WorldId,
            command.CandidateStateRevisionId,
            cancellationToken);
        if (candidateState is null ||
            !string.Equals(candidateState.AdapterId, world.AdapterId, StringComparison.Ordinal))
        {
            return new(CommitSharedWorldStatus.InvalidCandidate, world.Head, null);
        }

        if (candidateState.RequiredEnvironmentRevisionId is { } requiredEnvironment &&
            command.CandidateEnvironmentRevisionId != requiredEnvironment)
        {
            return new(CommitSharedWorldStatus.InvalidCandidate, world.Head, null);
        }

        if (command.CandidateEnvironmentRevisionId is { } candidateEnvironmentRevisionId)
        {
            var candidateEnvironment = await LoadEnvironmentCandidateAsync(
                connection,
                transaction,
                command.WorldId,
                candidateEnvironmentRevisionId,
                cancellationToken);
            if (candidateEnvironment is null ||
                !string.Equals(candidateEnvironment.AdapterId, world.AdapterId, StringComparison.Ordinal))
            {
                return new(CommitSharedWorldStatus.InvalidCandidate, world.Head, null);
            }
        }

        var candidateHead = new SharedWorldHead(
            command.CandidateStateRevisionId,
            command.CandidateEnvironmentRevisionId);
        var unchanged = candidateHead.EnvironmentRevisionId == world.Head.EnvironmentRevisionId &&
                        await HasSameStateContentAsync(
                            connection,
                            transaction,
                            command.WorldId,
                            world.Head.StateRevisionId,
                            candidateState,
                            cancellationToken);

        if (unchanged)
        {
            await DeleteReservationAsync(
                connection,
                transaction,
                command.WorldId,
                command.SessionId,
                command.Generation,
                cancellationToken);
            return new(CommitSharedWorldStatus.Unchanged, world.Head, candidateHead);
        }

        await UpdateWorldHeadAsync(
            connection,
            transaction,
            command.WorldId,
            candidateHead,
            serverNow,
            cancellationToken);
        await DeleteReservationAsync(
            connection,
            transaction,
            command.WorldId,
            command.SessionId,
            command.Generation,
            cancellationToken);
        return new(CommitSharedWorldStatus.Committed, candidateHead, candidateHead);
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
        command.Parameters.AddWithValue(
            "result_json",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(result));
        command.Parameters.AddWithValue("created_at", serverNow);
        command.Parameters.AddWithValue("expires_at", serverNow + IdempotencyLifetime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AcquireIdempotencyLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExternalIdentityRef caller,
        string operation,
        StewardIdempotencyKey key,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT pg_advisory_xact_lock(@lock_key);";
        var lockKey = CreateAdvisoryLockKey(caller, operation, key);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("lock_key", lockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteExpiredIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset serverNow,
        CancellationToken cancellationToken)
    {
        const string sql = "DELETE FROM steward_authority_idempotency WHERE expires_at <= @server_now;";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("server_now", serverNow);
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
        var hash = HashLogicalFieldsBytes(
            caller.Provider,
            caller.ExternalId,
            operation,
            key.Value);
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
            SELECT adapter_id, current_state_revision_id, current_environment_revision_id
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
        return new WorldRow(
            reader.GetString(reader.GetOrdinal("adapter_id")),
            new SharedWorldHead(
                new RevisionId(reader.GetGuid(reader.GetOrdinal("current_state_revision_id"))),
                reader.IsDBNull(environmentOrdinal)
                    ? null
                    : new RevisionId(reader.GetGuid(environmentOrdinal))));
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

        var startingEnvironmentOrdinal = reader.GetOrdinal("starting_environment_revision_id");
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
                reader.IsDBNull(startingEnvironmentOrdinal)
                    ? null
                    : new RevisionId(reader.GetGuid(startingEnvironmentOrdinal))),
            (SharedWorldReservationState)reader.GetInt16(reader.GetOrdinal("state")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("acquired_at")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("last_heartbeat_at")),
            reader.IsDBNull(uncertainOrdinal)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(uncertainOrdinal));
    }

    private static async Task<StateCandidate?> LoadStateCandidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT adapter_id, sha256, required_environment_revision_id
            FROM steward_state_revisions
            WHERE world_id = @world_id
              AND revision_id = @revision_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("revision_id", revisionId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var environmentOrdinal = reader.GetOrdinal("required_environment_revision_id");
        return new StateCandidate(
            reader.GetString(reader.GetOrdinal("adapter_id")),
            reader.GetString(reader.GetOrdinal("sha256")),
            reader.IsDBNull(environmentOrdinal)
                ? null
                : new RevisionId(reader.GetGuid(environmentOrdinal)));
    }

    private static async Task<EnvironmentCandidate?> LoadEnvironmentCandidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        RevisionId revisionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT adapter_id
            FROM steward_environment_revisions
            WHERE world_id = @world_id
              AND revision_id = @revision_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("revision_id", revisionId.Value);
        var adapterId = await command.ExecuteScalarAsync(cancellationToken) as string;
        return adapterId is null ? null : new EnvironmentCandidate(adapterId);
    }

    private static async Task<bool> HasSameStateContentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        RevisionId currentRevisionId,
        StateCandidate candidate,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT sha256
            FROM steward_state_revisions
            WHERE world_id = @world_id
              AND revision_id = @revision_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("revision_id", currentRevisionId.Value);
        var currentSha = await command.ExecuteScalarAsync(cancellationToken) as string;
        return currentSha is not null &&
               string.Equals(currentSha, candidate.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task UpdateWorldHeadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        SharedWorldHead head,
        DateTimeOffset serverNow,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE steward_shared_worlds
            SET current_state_revision_id = @state_revision_id,
                current_environment_revision_id = @environment_revision_id,
                updated_at = @updated_at
            WHERE world_id = @world_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("state_revision_id", head.StateRevisionId.Value);
        command.Parameters.Add(
            new NpgsqlParameter("environment_revision_id", NpgsqlDbType.Uuid)
            {
                Value = head.EnvironmentRevisionId is { } environment
                    ? environment.Value
                    : DBNull.Value
            });
        command.Parameters.AddWithValue("updated_at", serverNow);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("World head update did not affect exactly one row.");
        }
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
            throw new InvalidOperationException("Reservation resolution did not affect exactly one row.");
        }
    }

    private static void ValidateCommit(CommitSharedWorldCommand command)
    {
        if (command.WorldId.Value == Guid.Empty ||
            command.SessionId == Guid.Empty ||
            command.Generation <= 0 ||
            command.ExpectedHead.StateRevisionId.Value == Guid.Empty ||
            command.CandidateStateRevisionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Commit command contains an empty authority identifier.", nameof(command));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(command.InstallationId);
        if (command.InstallationId.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }
    }

    private static void ValidateIdentity(ExternalIdentityRef caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(caller.ExternalId);
    }

    private sealed record WorldRow(string AdapterId, SharedWorldHead Head);
    private sealed record StateCandidate(
        string AdapterId,
        string Sha256,
        RevisionId? RequiredEnvironmentRevisionId);
    private sealed record EnvironmentCandidate(string AdapterId);
}
