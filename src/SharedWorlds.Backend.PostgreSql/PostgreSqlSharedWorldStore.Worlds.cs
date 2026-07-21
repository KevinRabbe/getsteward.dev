using Npgsql;
using NpgsqlTypes;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.PostgreSql;

public sealed partial class PostgreSqlSharedWorldStore
{
    public async Task<bool> TryCreateWorldWithManagerAsync(
        SharedWorldMetadata world,
        SharedWorldMember accessManager,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(accessManager);

        const string insertWorld = """
            INSERT INTO steward_shared_worlds (
                world_id,
                adapter_id,
                display_name,
                current_state_revision_id,
                current_environment_revision_id,
                access_manager_provider,
                access_manager_external_id,
                created_at,
                updated_at)
            VALUES (
                @world_id,
                @adapter_id,
                @display_name,
                @current_state_revision_id,
                @current_environment_revision_id,
                @access_manager_provider,
                @access_manager_external_id,
                @created_at,
                @updated_at)
            ON CONFLICT (world_id) DO NOTHING;
            """;

        const string insertMember = """
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

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand(insertWorld, connection, transaction))
        {
            command.Parameters.AddWithValue("world_id", world.WorldId.Value);
            command.Parameters.AddWithValue("adapter_id", world.AdapterId);
            command.Parameters.AddWithValue("display_name", world.DisplayName);
            command.Parameters.AddWithValue("current_state_revision_id", world.CurrentStateRevisionId.Value);
            command.Parameters.Add(new NpgsqlParameter("current_environment_revision_id", NpgsqlDbType.Uuid)
            {
                Value = world.CurrentEnvironmentRevisionId is { } environmentRevision
                    ? environmentRevision.Value
                    : DBNull.Value
            });
            command.Parameters.AddWithValue("access_manager_provider", world.AccessManager.Provider);
            command.Parameters.AddWithValue("access_manager_external_id", world.AccessManager.ExternalId);
            command.Parameters.AddWithValue("created_at", world.CreatedAt);
            command.Parameters.AddWithValue("updated_at", world.UpdatedAt);

            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await using (var command = new NpgsqlCommand(insertMember, connection, transaction))
        {
            command.Parameters.AddWithValue("world_id", accessManager.WorldId.Value);
            command.Parameters.AddWithValue("provider", accessManager.Identity.Provider);
            command.Parameters.AddWithValue("external_id", accessManager.Identity.ExternalId);
            command.Parameters.AddWithValue("status", (short)accessManager.Status);
            command.Parameters.AddWithValue("added_at", accessManager.AddedAt);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<SharedWorldMetadata?> LoadWorldAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                world_id,
                adapter_id,
                display_name,
                current_state_revision_id,
                current_environment_revision_id,
                access_manager_provider,
                access_manager_external_id,
                created_at,
                updated_at
            FROM steward_shared_worlds
            WHERE world_id = @world_id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadWorld(reader) : null;
    }

    public async Task<SharedWorldMember?> LoadMemberAsync(
        WorldId worldId,
        ExternalIdentityRef identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        const string sql = """
            SELECT world_id, provider, external_id, status, added_at
            FROM steward_world_members
            WHERE world_id = @world_id
              AND provider = @provider
              AND external_id = @external_id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("provider", identity.Provider);
        command.Parameters.AddWithValue("external_id", identity.ExternalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadMember(reader) : null;
    }

    public async Task<IReadOnlyList<SharedWorldMetadata>> ListWorldsForActiveMemberAsync(
        ExternalIdentityRef identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        const string sql = """
            SELECT
                w.world_id,
                w.adapter_id,
                w.display_name,
                w.current_state_revision_id,
                w.current_environment_revision_id,
                w.access_manager_provider,
                w.access_manager_external_id,
                w.created_at,
                w.updated_at
            FROM steward_shared_worlds w
            INNER JOIN steward_world_members m
                ON m.world_id = w.world_id
            WHERE m.provider = @provider
              AND m.external_id = @external_id
              AND m.status = @active_status
            ORDER BY w.updated_at DESC, w.world_id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("provider", identity.Provider);
        command.Parameters.AddWithValue("external_id", identity.ExternalId);
        command.Parameters.AddWithValue("active_status", (short)SharedWorldMemberStatus.Active);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var worlds = new List<SharedWorldMetadata>();
        while (await reader.ReadAsync(cancellationToken))
        {
            worlds.Add(ReadWorld(reader));
        }

        return worlds;
    }
}
