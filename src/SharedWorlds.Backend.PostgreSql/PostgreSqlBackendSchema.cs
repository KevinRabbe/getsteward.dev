using Npgsql;

namespace SharedWorlds.Backend.PostgreSql;

public static class PostgreSqlBackendSchema
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS steward_shared_worlds (
            world_id uuid PRIMARY KEY,
            adapter_id text NOT NULL,
            display_name text NOT NULL,
            current_state_revision_id uuid NOT NULL,
            current_environment_revision_id uuid NULL,
            access_manager_provider text NOT NULL,
            access_manager_external_id text NOT NULL,
            created_at timestamptz NOT NULL,
            updated_at timestamptz NOT NULL
        );

        CREATE TABLE IF NOT EXISTS steward_world_members (
            world_id uuid NOT NULL REFERENCES steward_shared_worlds(world_id) ON DELETE CASCADE,
            provider text NOT NULL,
            external_id text NOT NULL,
            status smallint NOT NULL CHECK (status IN (0, 1)),
            added_at timestamptz NOT NULL,
            PRIMARY KEY (world_id, provider, external_id)
        );

        CREATE INDEX IF NOT EXISTS ix_steward_world_members_identity
            ON steward_world_members(provider, external_id, status);

        CREATE TABLE IF NOT EXISTS steward_environment_revisions (
            world_id uuid NOT NULL REFERENCES steward_shared_worlds(world_id) ON DELETE CASCADE,
            revision_id uuid NOT NULL,
            adapter_id text NOT NULL,
            artifact_reference text NOT NULL,
            byte_size bigint NULL CHECK (byte_size IS NULL OR byte_size > 0),
            sha256 text NULL,
            published_by_provider text NOT NULL,
            published_by_external_id text NOT NULL,
            published_at timestamptz NOT NULL,
            PRIMARY KEY (world_id, revision_id),
            CHECK ((byte_size IS NULL AND sha256 IS NULL) OR
                   (byte_size IS NOT NULL AND sha256 IS NOT NULL AND length(sha256) = 64))
        );

        CREATE TABLE IF NOT EXISTS steward_state_revisions (
            world_id uuid NOT NULL REFERENCES steward_shared_worlds(world_id) ON DELETE CASCADE,
            revision_id uuid NOT NULL,
            adapter_id text NOT NULL,
            package_object_key text NOT NULL,
            byte_size bigint NOT NULL CHECK (byte_size > 0),
            sha256 text NOT NULL CHECK (length(sha256) = 64),
            required_environment_revision_id uuid NULL,
            published_by_provider text NOT NULL,
            published_by_external_id text NOT NULL,
            published_at timestamptz NOT NULL,
            PRIMARY KEY (world_id, revision_id),
            FOREIGN KEY (world_id, required_environment_revision_id)
                REFERENCES steward_environment_revisions(world_id, revision_id)
        );
        """;

    public static async Task InitializeAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var command = dataSource.CreateCommand(SchemaSql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
