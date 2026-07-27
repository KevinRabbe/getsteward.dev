using Npgsql;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.PostgreSql;

public sealed class PostgreSqlSharedWorldPlayerPresenceStore : ISharedWorldPlayerPresenceStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlSharedWorldPlayerPresenceStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task UpsertAsync(
        SharedWorldPlayerPresence presence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presence);
        const string sql = """
            INSERT INTO steward_world_player_presence (
                world_id,
                player_provider,
                player_external_id,
                installation_id,
                updated_at)
            VALUES (
                @world_id,
                @player_provider,
                @player_external_id,
                @installation_id,
                @updated_at)
            ON CONFLICT (world_id, player_provider, player_external_id) DO UPDATE SET
                installation_id = EXCLUDED.installation_id,
                updated_at = EXCLUDED.updated_at
            WHERE steward_world_player_presence.updated_at <= EXCLUDED.updated_at;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", presence.WorldId.Value);
        command.Parameters.AddWithValue("player_provider", presence.Player.Provider);
        command.Parameters.AddWithValue("player_external_id", presence.Player.ExternalId);
        command.Parameters.AddWithValue("installation_id", presence.InstallationId);
        command.Parameters.AddWithValue("updated_at", presence.UpdatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SharedWorldPlayerPresence>> ListRecentAsync(
        WorldId worldId,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                player_provider,
                player_external_id,
                installation_id,
                updated_at
            FROM steward_world_player_presence
            WHERE world_id = @world_id
              AND updated_at >= @cutoff
            ORDER BY updated_at DESC;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("cutoff", cutoff);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<SharedWorldPlayerPresence>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SharedWorldPlayerPresence(
                worldId,
                new ExternalIdentityRef(reader.GetString(0), reader.GetString(1)),
                reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return rows;
    }

    public async Task<bool> DeleteAsync(
        WorldId worldId,
        ExternalIdentityRef player,
        string installationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        const string sql = """
            DELETE FROM steward_world_player_presence
            WHERE world_id = @world_id
              AND player_provider = @player_provider
              AND player_external_id = @player_external_id
              AND installation_id = @installation_id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("player_provider", player.Provider);
        command.Parameters.AddWithValue("player_external_id", player.ExternalId);
        command.Parameters.AddWithValue("installation_id", installationId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
