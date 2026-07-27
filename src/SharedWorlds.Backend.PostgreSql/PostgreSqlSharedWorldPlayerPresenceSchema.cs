using Npgsql;

namespace SharedWorlds.Backend.PostgreSql;

public static class PostgreSqlSharedWorldPlayerPresenceSchema
{
    public static async Task InitializeAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        const string sql = """
            CREATE TABLE IF NOT EXISTS steward_world_player_presence (
                world_id uuid NOT NULL,
                player_provider text NOT NULL,
                player_external_id text NOT NULL,
                installation_id text NOT NULL,
                updated_at timestamptz NOT NULL,
                PRIMARY KEY (world_id, player_provider, player_external_id),
                CONSTRAINT fk_steward_world_player_presence_world
                    FOREIGN KEY (world_id)
                    REFERENCES steward_shared_worlds(world_id)
                    ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_steward_world_player_presence_updated_at
                ON steward_world_player_presence(updated_at);
            """;

        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
