using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.PostgreSql;

/// <summary>
/// One-way PostgreSQL cutover from the legacy shared-World authority plane. The transaction locks
/// the canonical World row first, matching legacy acquire/commit/access mutation lock order, then
/// freezes the exact head and active membership, proves the access manager owns the exact live
/// reservation, persists the retirement tombstone, and removes that reservation before commit.
/// Database triggers prevent later legacy authority/access mutations.
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

    public async Task<LegacySharedWorldAuthorityRetirementResult?> GetAsync(
        ExternalIdentityRef caller,
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var retirement = await LoadRetirementAsync(
            connection,
            transaction,
            worldId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (retirement is null || retirement.Holder != caller)
        {
            return null;
        }

        return Result(
            LegacySharedWorldAuthorityRetirementStatus.AlreadyRetired,
            worldId,
            retirement);
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

        var world = await LockWorldAndReadAccessSnapshotAsync(
            connection,
            transaction,
            worldId,
            cancellationToken);
        if (world is null)
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

        if (world.AccessManager != caller)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Result(
                LegacySharedWorldAuthorityRetirementStatus.NotFoundOrUnauthorized,
                worldId);
        }

        if (!world.AccessStateReady)
        {
            await transaction.CommitAsync(cancellationToken);
            return Result(
                LegacySharedWorldAuthorityRetirementStatus.AccessStateNotReady,
                worldId);
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
            world.StateRevisionId,
            world.EnvironmentRevisionId,
            world.ActiveMembersFingerprint,
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

    private static async Task<WorldAuthoritySnapshot?> LockWorldAndReadAccessSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                current_state_revision_id,
                current_environment_revision_id,
                access_manager_provider,
                access_manager_external_id
            FROM steward_shared_worlds
            WHERE world_id = @world_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);

        RevisionId stateRevisionId;
        RevisionId? environmentRevisionId;
        ExternalIdentityRef accessManager;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var environmentOrdinal = reader.GetOrdinal("current_environment_revision_id");
            stateRevisionId = new RevisionId(
                reader.GetGuid(reader.GetOrdinal("current_state_revision_id")));
            environmentRevisionId = reader.IsDBNull(environmentOrdinal)
                ? null
                : new RevisionId(reader.GetGuid(environmentOrdinal));
            accessManager = new ExternalIdentityRef(
                reader.GetString(reader.GetOrdinal("access_manager_provider")),
                reader.GetString(reader.GetOrdinal("access_manager_external_id")));
        }

        var access = await LoadAccessSnapshotAsync(
            connection,
            transaction,
            worldId,
            accessManager,
            cancellationToken);
        return new WorldAuthoritySnapshot(
            stateRevisionId,
            environmentRevisionId,
            accessManager,
            access.ActiveMembersFingerprint,
            access.Ready);
    }

    private static async Task<AccessSnapshot> LoadAccessSnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        ExternalIdentityRef accessManager,
        CancellationToken cancellationToken)
    {
        const string memberSql = """
            SELECT provider, external_id, status
            FROM steward_world_members
            WHERE world_id = @world_id
            ORDER BY provider, external_id;
            """;
        var activeMembers = new List<(string Provider, string ExternalId)>();
        var allMembersActive = true;
        var managerIsActiveMember = false;
        await using (var command = new NpgsqlCommand(memberSql, connection, transaction))
        {
            command.Parameters.AddWithValue("world_id", worldId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var identity = new ExternalIdentityRef(reader.GetString(0), reader.GetString(1));
                var status = (SharedWorldMemberStatus)reader.GetInt16(2);
                if (status != SharedWorldMemberStatus.Active)
                {
                    allMembersActive = false;
                    continue;
                }

                activeMembers.Add((identity.Provider, identity.ExternalId));
                if (identity == accessManager)
                {
                    managerIsActiveMember = true;
                }
            }
        }

        const string invitationSql = """
            SELECT COUNT(*)
            FROM steward_world_invitations
            WHERE world_id = @world_id
              AND status = @pending_status;
            """;
        await using var invitationCommand = new NpgsqlCommand(
            invitationSql,
            connection,
            transaction);
        invitationCommand.Parameters.AddWithValue("world_id", worldId.Value);
        invitationCommand.Parameters.AddWithValue(
            "pending_status",
            (short)WorldAccessInvitationStatus.Pending);
        var pendingInvitations = Convert.ToInt64(
            await invitationCommand.ExecuteScalarAsync(cancellationToken));

        return new AccessSnapshot(
            StableIdentitySetFingerprint.Compute(activeMembers),
            Ready: allMembersActive && managerIsActiveMember && pendingInvitations == 0);
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
                state_revision_id,
                environment_revision_id,
                active_members_fingerprint,
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

        var environmentOrdinal = reader.GetOrdinal("environment_revision_id");
        return new RetirementRow(
            new ExternalIdentityRef(
                reader.GetString(reader.GetOrdinal("holder_provider")),
                reader.GetString(reader.GetOrdinal("holder_external_id"))),
            reader.GetString(reader.GetOrdinal("installation_id")),
            reader.GetGuid(reader.GetOrdinal("session_id")),
            reader.GetInt64(reader.GetOrdinal("generation")),
            new RevisionId(reader.GetGuid(reader.GetOrdinal("state_revision_id"))),
            reader.IsDBNull(environmentOrdinal)
                ? null
                : new RevisionId(reader.GetGuid(environmentOrdinal)),
            reader.GetString(reader.GetOrdinal("active_members_fingerprint")),
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
                state_revision_id,
                environment_revision_id,
                active_members_fingerprint,
                retired_at)
            VALUES (
                @world_id,
                @holder_provider,
                @holder_external_id,
                @installation_id,
                @session_id,
                @generation,
                @state_revision_id,
                @environment_revision_id,
                @active_members_fingerprint,
                @retired_at);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("holder_provider", retirement.Holder.Provider);
        command.Parameters.AddWithValue("holder_external_id", retirement.Holder.ExternalId);
        command.Parameters.AddWithValue("installation_id", retirement.InstallationId);
        command.Parameters.AddWithValue("session_id", retirement.SessionId);
        command.Parameters.AddWithValue("generation", retirement.Generation);
        command.Parameters.AddWithValue("state_revision_id", retirement.StateRevisionId.Value);
        command.Parameters.AddWithValue(
            "environment_revision_id",
            retirement.EnvironmentRevisionId is { } environment
                ? (object)environment.Value
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "active_members_fingerprint",
            retirement.ActiveMembersFingerprint);
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
            retirement?.StateRevisionId,
            retirement?.EnvironmentRevisionId,
            retirement?.ActiveMembersFingerprint,
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

    private sealed record AccessSnapshot(
        string ActiveMembersFingerprint,
        bool Ready);

    private sealed record WorldAuthoritySnapshot(
        RevisionId StateRevisionId,
        RevisionId? EnvironmentRevisionId,
        ExternalIdentityRef AccessManager,
        string ActiveMembersFingerprint,
        bool AccessStateReady);

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
        RevisionId StateRevisionId,
        RevisionId? EnvironmentRevisionId,
        string ActiveMembersFingerprint,
        DateTimeOffset RetiredAt);
}
