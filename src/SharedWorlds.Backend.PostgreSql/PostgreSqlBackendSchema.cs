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

        CREATE TABLE IF NOT EXISTS steward_world_invitations (
            invitation_id uuid PRIMARY KEY,
            world_id uuid NOT NULL REFERENCES steward_shared_worlds(world_id) ON DELETE CASCADE,
            invited_provider text NOT NULL,
            invited_external_id text NOT NULL,
            invited_by_provider text NOT NULL,
            invited_by_external_id text NOT NULL,
            status smallint NOT NULL CHECK (status IN (0, 1, 2)),
            created_at timestamptz NOT NULL,
            responded_at timestamptz NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_steward_pending_world_invitation
            ON steward_world_invitations(world_id, invited_provider, invited_external_id)
            WHERE status = 0;

        CREATE INDEX IF NOT EXISTS ix_steward_pending_invitations_identity
            ON steward_world_invitations(invited_provider, invited_external_id, created_at)
            WHERE status = 0;

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

        CREATE TABLE IF NOT EXISTS steward_auth_sessions (
            session_id uuid PRIMARY KEY,
            identity_provider text NOT NULL,
            identity_external_id text NOT NULL,
            display_name text NULL,
            installation_id text NOT NULL,
            refresh_token_hash text NOT NULL UNIQUE CHECK (length(refresh_token_hash) = 64),
            created_at timestamptz NOT NULL,
            refresh_expires_at timestamptz NOT NULL,
            revoked boolean NOT NULL DEFAULT false,
            revoked_at timestamptz NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_steward_active_session_installation
            ON steward_auth_sessions(installation_id)
            WHERE revoked = false;

        CREATE INDEX IF NOT EXISTS ix_steward_auth_session_refresh
            ON steward_auth_sessions(refresh_token_hash);

        CREATE TABLE IF NOT EXISTS steward_access_credentials (
            access_token_hash text PRIMARY KEY CHECK (length(access_token_hash) = 64),
            session_id uuid NOT NULL REFERENCES steward_auth_sessions(session_id) ON DELETE CASCADE,
            expires_at timestamptz NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_steward_access_credentials_session
            ON steward_access_credentials(session_id);
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
