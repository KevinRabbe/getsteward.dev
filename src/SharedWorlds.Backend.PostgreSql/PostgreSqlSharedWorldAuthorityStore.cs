using Npgsql;
using NpgsqlTypes;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.PostgreSql;

public sealed class PostgreSqlSharedWorldAuthorityStore :
    ISharedWorldAuthorityStore,
    ISharedWorldResponsibilityInspector
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlSharedWorldAuthorityStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<AcquireSharedWorldReservationResult> AcquireAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        string installationId,
        SharedWorldHead expectedHead,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(caller);
        ValidateWorldId(worldId);
        ValidateInstallationId(installationId);
        ValidateHead(expectedHead);
        ArgumentNullException.ThrowIfNull(options);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var world = await LoadWorldForUpdateAsync(connection, transaction, worldId, cancellationToken);
        if (world is null ||
            !await IsActiveMemberAsync(connection, transaction, worldId, caller, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
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
            await transaction.CommitAsync(cancellationToken);
            return new(AcquireSharedWorldReservationStatus.HeadChanged, null, world.Head);
        }

        var existing = await LoadReservationAsync(
            connection,
            transaction,
            worldId,
            forUpdate: true,
            cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
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

        await transaction.CommitAsync(cancellationToken);
        return new(AcquireSharedWorldReservationStatus.Acquired, reservation, world.Head);
    }

    public async Task<SharedWorldReservation?> GetReservationAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(caller);
        ValidateWorldId(worldId);
        ArgumentNullException.ThrowIfNull(options);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var world = await LoadWorldForUpdateAsync(connection, transaction, worldId, cancellationToken);
        if (world is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await RefreshUncertaintyAsync(
            connection,
            transaction,
            worldId,
            serverNow,
            options,
            cancellationToken);
        var reservation = await LoadReservationAsync(
            connection,
            transaction,
            worldId,
            forUpdate: false,
            cancellationToken);

        var activeMember = await IsActiveMemberAsync(
            connection,
            transaction,
            worldId,
            caller,
            cancellationToken);
        if (!activeMember && reservation?.Holder != caller)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return reservation;
    }

    public async Task<SharedWorldHeartbeatStatus> HeartbeatAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        string installationId,
        Guid sessionId,
        long generation,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(caller);
        ValidateWorldId(worldId);
        ValidateInstallationId(installationId);
        ValidateSession(sessionId, generation);
        ArgumentNullException.ThrowIfNull(options);

        const string sql = """
            UPDATE steward_world_reservations
            SET state = @active_state,
                last_heartbeat_at = @server_now,
                became_uncertain_at = NULL
            WHERE world_id = @world_id
              AND session_id = @session_id
              AND generation = @generation
              AND holder_provider = @holder_provider
              AND holder_external_id = @holder_external_id
              AND installation_id = @installation_id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("active_state", (short)SharedWorldReservationState.Active);
        command.Parameters.AddWithValue("server_now", serverNow);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("session_id", sessionId);
        command.Parameters.AddWithValue("generation", generation);
        command.Parameters.AddWithValue("holder_provider", caller.Provider);
        command.Parameters.AddWithValue("holder_external_id", caller.ExternalId);
        command.Parameters.AddWithValue("installation_id", installationId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1
            ? SharedWorldHeartbeatStatus.Accepted
            : SharedWorldHeartbeatStatus.ReservationMismatch;
    }

    public async Task<ReclaimSharedWorldReservationResult> ReclaimAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        Guid expectedSessionId,
        long expectedGeneration,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(caller);
        ValidateWorldId(worldId);
        ValidateSession(expectedSessionId, expectedGeneration);
        ArgumentNullException.ThrowIfNull(options);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var world = await LoadWorldForUpdateAsync(connection, transaction, worldId, cancellationToken);
        if (world is null ||
            !await IsActiveMemberAsync(connection, transaction, worldId, caller, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(ReclaimSharedWorldReservationStatus.NotFoundOrUnauthorized, null, null);
        }

        await RefreshUncertaintyAsync(
            connection,
            transaction,
            worldId,
            serverNow,
            options,
            cancellationToken);
        var reservation = await LoadReservationAsync(
            connection,
            transaction,
            worldId,
            forUpdate: true,
            cancellationToken);

        if (reservation is null ||
            reservation.SessionId != expectedSessionId ||
            reservation.Generation != expectedGeneration ||
            reservation.State != SharedWorldReservationState.Uncertain ||
            reservation.BecameUncertainAt is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(ReclaimSharedWorldReservationStatus.ReservationMismatch, null, world.Head);
        }

        if (serverNow - reservation.BecameUncertainAt.Value < options.ReclaimAfterUncertain)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(ReclaimSharedWorldReservationStatus.GracePeriodRequired, null, world.Head);
        }

        await DeleteReservationAsync(
            connection,
            transaction,
            worldId,
            expectedSessionId,
            expectedGeneration,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(ReclaimSharedWorldReservationStatus.Reclaimed, expectedGeneration, world.Head);
    }

    public async Task<CommitSharedWorldResult> CommitAsync(
        ExternalIdentityRef caller,
        CommitSharedWorldCommand command,
        DateTimeOffset serverNow,
        SharedWorldAuthorityOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(caller);
        ArgumentNullException.ThrowIfNull(command);
        ValidateWorldId(command.WorldId);
        ValidateInstallationId(command.InstallationId);
        ValidateSession(command.SessionId, command.Generation);
        ValidateHead(command.ExpectedHead);
        if (command.CandidateStateRevisionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Candidate state revision is required.", nameof(command));
        }

        ArgumentNullException.ThrowIfNull(options);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var world = await LoadWorldForUpdateAsync(
            connection,
            transaction,
            command.WorldId,
            cancellationToken);
        if (world is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("Reservation references a World that no longer exists.");
        }

        var reservation = await LoadReservationAsync(
            connection,
            transaction,
            command.WorldId,
            forUpdate: true,
            cancellationToken);
        if (reservation is null ||
            reservation.SessionId != command.SessionId ||
            reservation.Generation != command.Generation ||
            reservation.Holder != caller ||
            !string.Equals(reservation.InstallationId, command.InstallationId, StringComparison.Ordinal))
        {
            await transaction.CommitAsync(cancellationToken);
            return new(CommitSharedWorldStatus.ReservationMismatch, world.Head, null);
        }

        if (world.Head != command.ExpectedHead || reservation.StartingHead != command.ExpectedHead)
        {
            await transaction.CommitAsync(cancellationToken);
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
            await transaction.CommitAsync(cancellationToken);
            return new(CommitSharedWorldStatus.InvalidCandidate, world.Head, null);
        }

        if (candidateState.RequiredEnvironmentRevisionId is { } requiredEnvironment &&
            command.CandidateEnvironmentRevisionId != requiredEnvironment)
        {
            await transaction.CommitAsync(cancellationToken);
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
                await transaction.CommitAsync(cancellationToken);
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
            await transaction.CommitAsync(cancellationToken);
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
        await transaction.CommitAsync(cancellationToken);
        return new(CommitSharedWorldStatus.Committed, candidateHead, candidateHead);
    }

    public async Task<bool> HasUnresolvedWritableResponsibilityAsync(
        WorldId worldId,
        ExternalIdentityRef identity,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        ValidateIdentity(identity);
        const string sql = """
            SELECT EXISTS (
                SELECT 1
                FROM steward_world_reservations
                WHERE world_id = @world_id
                  AND holder_provider = @holder_provider
                  AND holder_external_id = @holder_external_id);
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("holder_provider", identity.Provider);
        command.Parameters.AddWithValue("holder_external_id", identity.ExternalId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task<WorldRow?> LoadWorldForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                adapter_id,
                current_state_revision_id,
                current_environment_revision_id,
                reservation_generation
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
                    : new RevisionId(reader.GetGuid(environmentOrdinal))),
            reader.GetInt64(reader.GetOrdinal("reservation_generation")));
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

    private static async Task<SharedWorldReservation?> LoadReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var sql = """
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
            """ + (forUpdate ? " FOR UPDATE;" : ";");

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
            throw new InvalidOperationException("Reservation changed during an authority transaction.");
        }
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
            revisionId,
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
        var adapter = await command.ExecuteScalarAsync(cancellationToken) as string;
        return adapter is null ? null : new EnvironmentCandidate(revisionId, adapter);
    }

    private static async Task<bool> HasSameStateContentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        RevisionId currentRevisionId,
        StateCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (currentRevisionId == candidate.RevisionId)
        {
            return true;
        }

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
        command.Parameters.Add(new NpgsqlParameter("environment_revision_id", NpgsqlDbType.Uuid)
        {
            Value = head.EnvironmentRevisionId is { } environment
                ? environment.Value
                : DBNull.Value
        });
        command.Parameters.AddWithValue("updated_at", serverNow);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("World disappeared during an authority transaction.");
        }
    }

    private static void ValidateIdentity(ExternalIdentityRef identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
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

    private sealed record WorldRow(
        string AdapterId,
        SharedWorldHead Head,
        long ReservationGeneration);

    private sealed record StateCandidate(
        RevisionId RevisionId,
        string AdapterId,
        string Sha256,
        RevisionId? RequiredEnvironmentRevisionId);

    private sealed record EnvironmentCandidate(
        RevisionId RevisionId,
        string AdapterId);
}
