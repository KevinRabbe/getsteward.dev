using Npgsql;

namespace SharedWorlds.Backend.PostgreSql;

public static class PostgreSqlSharedWorldHostPresenceSchema
{
    public static async Task InitializeAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        const string sql = """
            CREATE TABLE IF NOT EXISTS steward_world_host_presence (
                world_id uuid PRIMARY KEY,
                session_id uuid NOT NULL,
                generation bigint NOT NULL CHECK (generation > 0),
                holder_provider text NOT NULL,
                holder_external_id text NOT NULL,
                installation_id text NOT NULL,
                state smallint NOT NULL CHECK (state IN (0, 1)),
                address text NULL,
                port integer NULL CHECK (port IS NULL OR (port >= 1 AND port <= 65535)),
                join_token text NULL,
                updated_at timestamptz NOT NULL,
                CONSTRAINT fk_steward_world_host_presence_world
                    FOREIGN KEY (world_id)
                    REFERENCES steward_shared_worlds(world_id)
                    ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_steward_world_host_presence_updated_at
                ON steward_world_host_presence(updated_at);
            """;

        await using var command = dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
