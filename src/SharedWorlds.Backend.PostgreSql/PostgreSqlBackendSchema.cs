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
            updated_at timestamptz NOT NULL,
            reservation_generation bigint NOT NULL DEFAULT 0 CHECK (reservation_generation >= 0)
        );

        ALTER TABLE steward_shared_worlds
            ADD COLUMN IF NOT EXISTS reservation_generation bigint NOT NULL DEFAULT 0;

        CREATE TABLE IF NOT EXISTS steward_world_canonical_heads (
            world_id uuid NOT NULL REFERENCES steward_shared_worlds(world_id) ON DELETE CASCADE,
            sequence bigint NOT NULL CHECK (sequence >= 0),
            state_revision_id uuid NOT NULL,
            environment_revision_id uuid NULL,
            committed_at timestamptz NOT NULL,
            PRIMARY KEY (world_id, sequence)
        );

        CREATE INDEX IF NOT EXISTS ix_steward_world_canonical_heads_recent
            ON steward_world_canonical_heads(world_id, sequence DESC);

        CREATE OR REPLACE FUNCTION steward_record_canonical_head()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        DECLARE
            next_sequence bigint;
        BEGIN
            IF TG_OP = 'UPDATE' AND
               NEW.current_state_revision_id IS NOT DISTINCT FROM OLD.current_state_revision_id AND
               NEW.current_environment_revision_id IS NOT DISTINCT FROM OLD.current_environment_revision_id THEN
                RETURN NEW;
            END IF;

            SELECT COALESCE(MAX(sequence), -1) + 1
            INTO next_sequence
            FROM steward_world_canonical_heads
            WHERE world_id = NEW.world_id;

            INSERT INTO steward_world_canonical_heads (
                world_id,
                sequence,
                state_revision_id,
                environment_revision_id,
                committed_at)
            VALUES (
                NEW.world_id,
                next_sequence,
                NEW.current_state_revision_id,
                NEW.current_environment_revision_id,
                CASE WHEN TG_OP = 'INSERT' THEN NEW.created_at ELSE NEW.updated_at END);

            RETURN NEW;
        END;
        $$;

        DROP TRIGGER IF EXISTS trg_steward_record_canonical_head ON steward_shared_worlds;
        CREATE TRIGGER trg_steward_record_canonical_head
            AFTER INSERT OR UPDATE OF current_state_revision_id, current_environment_revision_id
            ON steward_shared_worlds
            FOR EACH ROW
            EXECUTE FUNCTION steward_record_canonical_head();

        INSERT INTO steward_world_canonical_heads (
            world_id,
            sequence,
            state_revision_id,
            environment_revision_id,
            committed_at)
        SELECT
            w.world_id,
            0,
            w.current_state_revision_id,
            w.current_environment_revision_id,
            w.updated_at
        FROM steward_shared_worlds w
        WHERE NOT EXISTS (
            SELECT 1
            FROM steward_world_canonical_heads h
            WHERE h.world_id = w.world_id)
        ON CONFLICT (world_id, sequence) DO NOTHING;

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

        CREATE TABLE IF NOT EXISTS steward_package_transfers (
            transfer_id uuid PRIMARY KEY,
            world_id uuid NOT NULL REFERENCES steward_shared_worlds(world_id) ON DELETE CASCADE,
            revision_id uuid NOT NULL,
            kind smallint NOT NULL CHECK (kind IN (0, 1)),
            adapter_id text NOT NULL,
            owner_provider text NOT NULL,
            owner_external_id text NOT NULL,
            object_key text NOT NULL,
            provider_upload_id text NOT NULL UNIQUE,
            expected_byte_size bigint NOT NULL CHECK (expected_byte_size > 0),
            expected_sha256 text NOT NULL CHECK (length(expected_sha256) = 64),
            required_environment_revision_id uuid NULL,
            part_size_bytes integer NOT NULL CHECK (part_size_bytes > 0),
            part_count integer NOT NULL CHECK (part_count > 0),
            created_at timestamptz NOT NULL,
            expires_at timestamptz NOT NULL,
            state smallint NOT NULL CHECK (state IN (0, 1, 2, 3, 4)),
            finalized_at timestamptz NULL,
            FOREIGN KEY (world_id, required_environment_revision_id)
                REFERENCES steward_environment_revisions(world_id, revision_id)
        );

        CREATE INDEX IF NOT EXISTS ix_steward_package_transfers_owner
            ON steward_package_transfers(owner_provider, owner_external_id, state);

        CREATE INDEX IF NOT EXISTS ix_steward_package_transfers_expiry
            ON steward_package_transfers(state, expires_at);

        CREATE TABLE IF NOT EXISTS steward_object_cleanup_queue (
            object_key text PRIMARY KEY,
            enqueued_at timestamptz NOT NULL,
            attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
            last_attempt_at timestamptz NULL
        );

        CREATE INDEX IF NOT EXISTS ix_steward_object_cleanup_queue_retry
            ON steward_object_cleanup_queue(last_attempt_at, enqueued_at);

        CREATE TABLE IF NOT EXISTS steward_world_reservations (
            world_id uuid PRIMARY KEY REFERENCES steward_shared_worlds(world_id) ON DELETE CASCADE,
            session_id uuid NOT NULL UNIQUE,
            generation bigint NOT NULL CHECK (generation > 0),
            holder_provider text NOT NULL,
            holder_external_id text NOT NULL,
            installation_id text NOT NULL,
            starting_state_revision_id uuid NOT NULL,
            starting_environment_revision_id uuid NULL,
            state smallint NOT NULL CHECK (state IN (0, 1)),
            acquired_at timestamptz NOT NULL,
            last_heartbeat_at timestamptz NOT NULL,
            became_uncertain_at timestamptz NULL,
            CHECK ((state = 0 AND became_uncertain_at IS NULL) OR
                   (state = 1 AND became_uncertain_at IS NOT NULL))
        );

        CREATE INDEX IF NOT EXISTS ix_steward_world_reservations_holder
            ON steward_world_reservations(holder_provider, holder_external_id);

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
