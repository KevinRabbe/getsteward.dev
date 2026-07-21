using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.PostgreSql;

public sealed class PostgreSqlSharedWorldAccessStore : ISharedWorldAccessStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlSharedWorldAccessStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<SharedWorldMember>> ListMembersAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT world_id, provider, external_id, status, added_at
            FROM steward_world_members
            WHERE world_id = @world_id
            ORDER BY added_at, provider, external_id;
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var members = new List<SharedWorldMember>();
        while (await reader.ReadAsync(cancellationToken))
        {
            members.Add(ReadMember(reader));
        }

        return members;
    }

    public async Task<IReadOnlyList<WorldAccessInvitation>> ListPendingInvitationsForIdentityAsync(
        ExternalIdentityRef identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        const string sql = """
            SELECT
                invitation_id,
                world_id,
                invited_provider,
                invited_external_id,
                invited_by_provider,
                invited_by_external_id,
                status,
                created_at,
                responded_at
            FROM steward_world_invitations
            WHERE invited_provider = @provider
              AND invited_external_id = @external_id
              AND status = @pending_status
            ORDER BY created_at, invitation_id;
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("provider", identity.Provider);
        command.Parameters.AddWithValue("external_id", identity.ExternalId);
        command.Parameters.AddWithValue("pending_status", (short)WorldAccessInvitationStatus.Pending);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var invitations = new List<WorldAccessInvitation>();
        while (await reader.ReadAsync(cancellationToken))
        {
            invitations.Add(ReadInvitation(reader));
        }

        return invitations;
    }

    public async Task<StoreCreateInvitationStatus> TryCreateInvitationAsync(
        WorldAccessInvitation invitation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockWorldAsync(connection, transaction, invitation.WorldId, cancellationToken);

        const string memberSql = """
            SELECT 1
            FROM steward_world_members
            WHERE world_id = @world_id
              AND provider = @provider
              AND external_id = @external_id;
            """;
        await using (var memberCommand = new NpgsqlCommand(memberSql, connection, transaction))
        {
            memberCommand.Parameters.AddWithValue("world_id", invitation.WorldId.Value);
            memberCommand.Parameters.AddWithValue("provider", invitation.InvitedIdentity.Provider);
            memberCommand.Parameters.AddWithValue("external_id", invitation.InvitedIdentity.ExternalId);
            if (await memberCommand.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return StoreCreateInvitationStatus.TargetAlreadyMember;
            }
        }

        const string insertSql = """
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
                NULL)
            ON CONFLICT (world_id, invited_provider, invited_external_id)
                WHERE status = 0
                DO NOTHING;
            """;
        await using var insertCommand = new NpgsqlCommand(insertSql, connection, transaction);
        insertCommand.Parameters.AddWithValue("invitation_id", invitation.Id.Value);
        insertCommand.Parameters.AddWithValue("world_id", invitation.WorldId.Value);
        insertCommand.Parameters.AddWithValue("invited_provider", invitation.InvitedIdentity.Provider);
        insertCommand.Parameters.AddWithValue("invited_external_id", invitation.InvitedIdentity.ExternalId);
        insertCommand.Parameters.AddWithValue("invited_by_provider", invitation.InvitedBy.Provider);
        insertCommand.Parameters.AddWithValue("invited_by_external_id", invitation.InvitedBy.ExternalId);
        insertCommand.Parameters.AddWithValue("status", (short)WorldAccessInvitationStatus.Pending);
        insertCommand.Parameters.AddWithValue("created_at", invitation.CreatedAt);
        var inserted = await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return inserted == 1
            ? StoreCreateInvitationStatus.Created
            : StoreCreateInvitationStatus.AlreadyInvited;
    }

    public Task<StoreInvitationResponseStatus> TryAcceptInvitationAsync(
        WorldAccessInvitationId invitationId,
        ExternalIdentityRef invitedIdentity,
        DateTimeOffset respondedAt,
        CancellationToken cancellationToken = default)
        => RespondToInvitationAsync(
            invitationId,
            invitedIdentity,
            respondedAt,
            accept: true,
            cancellationToken);

    public Task<StoreInvitationResponseStatus> TryDeclineInvitationAsync(
        WorldAccessInvitationId invitationId,
        ExternalIdentityRef invitedIdentity,
        DateTimeOffset respondedAt,
        CancellationToken cancellationToken = default)
        => RespondToInvitationAsync(
            invitationId,
            invitedIdentity,
            respondedAt,
            accept: false,
            cancellationToken);

    public async Task<StoreMemberRevocationStatus> TryRevokeMemberAsync(
        WorldId worldId,
        ExternalIdentityRef expectedAccessManager,
        ExternalIdentityRef targetIdentity,
        bool deferForUnresolvedResponsibility,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedAccessManager);
        ArgumentNullException.ThrowIfNull(targetIdentity);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var manager = await LockWorldAsync(connection, transaction, worldId, cancellationToken);
        if (manager != expectedAccessManager)
        {
            await transaction.RollbackAsync(cancellationToken);
            return StoreMemberRevocationStatus.ManagerChanged;
        }

        var member = await LoadMemberForUpdateAsync(
            connection,
            transaction,
            worldId,
            targetIdentity,
            cancellationToken);
        if (member?.Status != SharedWorldMemberStatus.Active)
        {
            await transaction.RollbackAsync(cancellationToken);
            return StoreMemberRevocationStatus.TargetNotActiveMember;
        }

        if (deferForUnresolvedResponsibility)
        {
            const string updateMember = """
                UPDATE steward_world_members
                SET status = @status
                WHERE world_id = @world_id
                  AND provider = @provider
                  AND external_id = @external_id;
                """;
            await using var command = new NpgsqlCommand(updateMember, connection, transaction);
            command.Parameters.AddWithValue("status", (short)SharedWorldMemberStatus.RevocationPending);
            command.Parameters.AddWithValue("world_id", worldId.Value);
            command.Parameters.AddWithValue("provider", targetIdentity.Provider);
            command.Parameters.AddWithValue("external_id", targetIdentity.ExternalId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await DeleteMemberAsync(connection, transaction, worldId, targetIdentity, cancellationToken);
        }

        await TouchWorldAsync(connection, transaction, worldId, changedAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deferForUnresolvedResponsibility
            ? StoreMemberRevocationStatus.RevocationPending
            : StoreMemberRevocationStatus.Revoked;
    }

    public async Task<StoreLeaveMemberStatus> TryLeaveWorldAsync(
        WorldId worldId,
        ExternalIdentityRef identity,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var manager = await LockWorldAsync(connection, transaction, worldId, cancellationToken);
        if (manager == identity)
        {
            await transaction.RollbackAsync(cancellationToken);
            return StoreLeaveMemberStatus.IsAccessManager;
        }

        var member = await LoadMemberForUpdateAsync(
            connection,
            transaction,
            worldId,
            identity,
            cancellationToken);
        if (member?.Status != SharedWorldMemberStatus.Active)
        {
            await transaction.RollbackAsync(cancellationToken);
            return StoreLeaveMemberStatus.TargetNotActiveMember;
        }

        await DeleteMemberAsync(connection, transaction, worldId, identity, cancellationToken);
        await TouchWorldAsync(connection, transaction, worldId, changedAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return StoreLeaveMemberStatus.Left;
    }

    public async Task<StoreCompletePendingRevocationStatus> TryCompletePendingRevocationAsync(
        WorldId worldId,
        ExternalIdentityRef identity,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockWorldAsync(connection, transaction, worldId, cancellationToken);
        var member = await LoadMemberForUpdateAsync(
            connection,
            transaction,
            worldId,
            identity,
            cancellationToken);
        if (member?.Status != SharedWorldMemberStatus.RevocationPending)
        {
            await transaction.RollbackAsync(cancellationToken);
            return StoreCompletePendingRevocationStatus.NotPending;
        }

        await DeleteMemberAsync(connection, transaction, worldId, identity, cancellationToken);
        await TouchWorldAsync(connection, transaction, worldId, changedAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return StoreCompletePendingRevocationStatus.Completed;
    }

    public async Task<StoreTransferAccessManagerStatus> TryTransferAccessManagerAsync(
        WorldId worldId,
        ExternalIdentityRef expectedAccessManager,
        ExternalIdentityRef targetIdentity,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedAccessManager);
        ArgumentNullException.ThrowIfNull(targetIdentity);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var manager = await LockWorldAsync(connection, transaction, worldId, cancellationToken);
        if (manager != expectedAccessManager)
        {
            await transaction.RollbackAsync(cancellationToken);
            return StoreTransferAccessManagerStatus.ManagerChanged;
        }

        if (manager == targetIdentity)
        {
            await transaction.RollbackAsync(cancellationToken);
            return StoreTransferAccessManagerStatus.AlreadyManager;
        }

        var target = await LoadMemberForUpdateAsync(
            connection,
            transaction,
            worldId,
            targetIdentity,
            cancellationToken);
        if (target?.Status != SharedWorldMemberStatus.Active)
        {
            await transaction.RollbackAsync(cancellationToken);
            return StoreTransferAccessManagerStatus.TargetNotActiveMember;
        }

        const string updateSql = """
            UPDATE steward_shared_worlds
            SET access_manager_provider = @provider,
                access_manager_external_id = @external_id,
                updated_at = @updated_at
            WHERE world_id = @world_id;
            """;
        await using var command = new NpgsqlCommand(updateSql, connection, transaction);
        command.Parameters.AddWithValue("provider", targetIdentity.Provider);
        command.Parameters.AddWithValue("external_id", targetIdentity.ExternalId);
        command.Parameters.AddWithValue("updated_at", changedAt);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return StoreTransferAccessManagerStatus.Transferred;
    }

    private async Task<StoreInvitationResponseStatus> RespondToInvitationAsync(
        WorldAccessInvitationId invitationId,
        ExternalIdentityRef invitedIdentity,
        DateTimeOffset respondedAt,
        bool accept,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invitedIdentity);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string invitationSql = """
            SELECT
                invitation_id,
                world_id,
                invited_provider,
                invited_external_id,
                invited_by_provider,
                invited_by_external_id,
                status,
                created_at,
                responded_at
            FROM steward_world_invitations
            WHERE invitation_id = @invitation_id
              AND invited_provider = @provider
              AND invited_external_id = @external_id
            FOR UPDATE;
            """;
        WorldAccessInvitation? invitation;
        await using (var command = new NpgsqlCommand(invitationSql, connection, transaction))
        {
            command.Parameters.AddWithValue("invitation_id", invitationId.Value);
            command.Parameters.AddWithValue("provider", invitedIdentity.Provider);
            command.Parameters.AddWithValue("external_id", invitedIdentity.ExternalId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            invitation = await reader.ReadAsync(cancellationToken) ? ReadInvitation(reader) : null;
        }

        if (invitation is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return StoreInvitationResponseStatus.NotFoundOrNotInvited;
        }

        if (invitation.Status != WorldAccessInvitationStatus.Pending)
        {
            await transaction.RollbackAsync(cancellationToken);
            return StoreInvitationResponseStatus.AlreadyResolved;
        }

        await LockWorldAsync(connection, transaction, invitation.WorldId, cancellationToken);
        if (accept)
        {
            const string memberSql = """
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
                    @added_at)
                ON CONFLICT (world_id, provider, external_id) DO NOTHING;
                """;
            await using var memberCommand = new NpgsqlCommand(memberSql, connection, transaction);
            memberCommand.Parameters.AddWithValue("world_id", invitation.WorldId.Value);
            memberCommand.Parameters.AddWithValue("provider", invitedIdentity.Provider);
            memberCommand.Parameters.AddWithValue("external_id", invitedIdentity.ExternalId);
            memberCommand.Parameters.AddWithValue("status", (short)SharedWorldMemberStatus.Active);
            memberCommand.Parameters.AddWithValue("added_at", respondedAt);
            await memberCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string responseSql = """
            UPDATE steward_world_invitations
            SET status = @status,
                responded_at = @responded_at
            WHERE invitation_id = @invitation_id;
            """;
        await using var responseCommand = new NpgsqlCommand(responseSql, connection, transaction);
        responseCommand.Parameters.AddWithValue(
            "status",
            (short)(accept ? WorldAccessInvitationStatus.Accepted : WorldAccessInvitationStatus.Declined));
        responseCommand.Parameters.AddWithValue("responded_at", respondedAt);
        responseCommand.Parameters.AddWithValue("invitation_id", invitationId.Value);
        await responseCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return StoreInvitationResponseStatus.Completed;
    }

    private static async Task<ExternalIdentityRef> LockWorldAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT access_manager_provider, access_manager_external_id
            FROM steward_shared_worlds
            WHERE world_id = @world_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Shared World disappeared during access transaction.");
        }

        return new ExternalIdentityRef(reader.GetString(0), reader.GetString(1));
    }

    private static async Task<SharedWorldMember?> LoadMemberForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        ExternalIdentityRef identity,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT world_id, provider, external_id, status, added_at
            FROM steward_world_members
            WHERE world_id = @world_id
              AND provider = @provider
              AND external_id = @external_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("provider", identity.Provider);
        command.Parameters.AddWithValue("external_id", identity.ExternalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMember(reader) : null;
    }

    private static async Task DeleteMemberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        ExternalIdentityRef identity,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM steward_world_members
            WHERE world_id = @world_id
              AND provider = @provider
              AND external_id = @external_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("provider", identity.Provider);
        command.Parameters.AddWithValue("external_id", identity.ExternalId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TouchWorldAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE steward_shared_worlds
            SET updated_at = @updated_at
            WHERE world_id = @world_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("updated_at", changedAt);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SharedWorldMember ReadMember(NpgsqlDataReader reader)
        => new(
            new WorldId(reader.GetGuid(reader.GetOrdinal("world_id"))),
            new ExternalIdentityRef(
                reader.GetString(reader.GetOrdinal("provider")),
                reader.GetString(reader.GetOrdinal("external_id"))),
            (SharedWorldMemberStatus)reader.GetInt16(reader.GetOrdinal("status")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("added_at")));

    private static WorldAccessInvitation ReadInvitation(NpgsqlDataReader reader)
    {
        var respondedOrdinal = reader.GetOrdinal("responded_at");
        return new WorldAccessInvitation(
            new WorldAccessInvitationId(reader.GetGuid(reader.GetOrdinal("invitation_id"))),
            new WorldId(reader.GetGuid(reader.GetOrdinal("world_id"))),
            new ExternalIdentityRef(
                reader.GetString(reader.GetOrdinal("invited_provider")),
                reader.GetString(reader.GetOrdinal("invited_external_id"))),
            new ExternalIdentityRef(
                reader.GetString(reader.GetOrdinal("invited_by_provider")),
                reader.GetString(reader.GetOrdinal("invited_by_external_id"))),
            (WorldAccessInvitationStatus)reader.GetInt16(reader.GetOrdinal("status")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            reader.IsDBNull(respondedOrdinal)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(respondedOrdinal));
    }
}
