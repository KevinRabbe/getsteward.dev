using Npgsql;
using NpgsqlTypes;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.PostgreSql;

public sealed class PostgreSqlSharedWorldHostPresenceStore : ISharedWorldHostPresenceStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlSharedWorldHostPresenceStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<bool> TryUpsertAsync(
        SharedWorldHostPresence presence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presence);
        const string sql = """
            INSERT INTO steward_world_host_presence (
                world_id,
                session_id,
                generation,
                holder_provider,
                holder_external_id,
                installation_id,
                state,
                address,
                port,
                join_token,
                updated_at)
            SELECT
                @world_id,
                @session_id,
                @generation,
                @holder_provider,
                @holder_external_id,
                @installation_id,
                @state,
                @address,
                @port,
                @join_token,
                @updated_at
            WHERE EXISTS (
                SELECT 1
                FROM steward_world_reservations
                WHERE world_id = @world_id
                  AND session_id = @session_id
                  AND generation = @generation
                  AND holder_provider = @holder_provider
                  AND holder_external_id = @holder_external_id
                  AND installation_id = @installation_id
                  AND state = @active_reservation_state)
            ON CONFLICT (world_id) DO UPDATE SET
                session_id = EXCLUDED.session_id,
                generation = EXCLUDED.generation,
                holder_provider = EXCLUDED.holder_provider,
                holder_external_id = EXCLUDED.holder_external_id,
                installation_id = EXCLUDED.installation_id,
                state = EXCLUDED.state,
                address = EXCLUDED.address,
                port = EXCLUDED.port,
                join_token = EXCLUDED.join_token,
                updated_at = EXCLUDED.updated_at
            WHERE steward_world_host_presence.generation < EXCLUDED.generation
               OR (
                    steward_world_host_presence.generation = EXCLUDED.generation
                    AND steward_world_host_presence.session_id = EXCLUDED.session_id
                    AND steward_world_host_presence.updated_at <= EXCLUDED.updated_at
               );
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", presence.WorldId.Value);
        command.Parameters.AddWithValue("session_id", presence.SessionId);
        command.Parameters.AddWithValue("generation", presence.Generation);
        command.Parameters.AddWithValue("holder_provider", presence.Holder.Provider);
        command.Parameters.AddWithValue("holder_external_id", presence.Holder.ExternalId);
        command.Parameters.AddWithValue("installation_id", presence.InstallationId);
        command.Parameters.AddWithValue("state", (short)presence.State);
        command.Parameters.AddWithValue(
            "address",
            NpgsqlDbType.Text,
            presence.Address is null ? DBNull.Value : presence.Address);
        command.Parameters.AddWithValue(
            "port",
            NpgsqlDbType.Integer,
            presence.Port is null ? DBNull.Value : presence.Port.Value);
        command.Parameters.AddWithValue(
            "join_token",
            NpgsqlDbType.Text,
            presence.JoinToken is null ? DBNull.Value : presence.JoinToken);
        command.Parameters.AddWithValue("updated_at", presence.UpdatedAt);
        command.Parameters.AddWithValue(
            "active_reservation_state",
            (short)SharedWorldReservationState.Active);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<SharedWorldHostPresence?> GetAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                session_id,
                generation,
                holder_provider,
                holder_external_id,
                installation_id,
                state,
                address,
                port,
                join_token,
                updated_at
            FROM steward_world_host_presence
            WHERE world_id = @world_id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SharedWorldHostPresence(
            worldId,
            reader.GetGuid(0),
            reader.GetInt64(1),
            new ExternalIdentityRef(reader.GetString(2), reader.GetString(3)),
            reader.GetString(4),
            (SharedWorldHostPresenceState)reader.GetInt16(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9));
    }

    public async Task<bool> DeleteAsync(
        WorldId worldId,
        ExternalIdentityRef holder,
        Guid sessionId,
        long generation,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM steward_world_host_presence
            WHERE world_id = @world_id
              AND session_id = @session_id
              AND generation = @generation
              AND holder_provider = @holder_provider
              AND holder_external_id = @holder_external_id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("session_id", sessionId);
        command.Parameters.AddWithValue("generation", generation);
        command.Parameters.AddWithValue("holder_provider", holder.Provider);
        command.Parameters.AddWithValue("holder_external_id", holder.ExternalId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
