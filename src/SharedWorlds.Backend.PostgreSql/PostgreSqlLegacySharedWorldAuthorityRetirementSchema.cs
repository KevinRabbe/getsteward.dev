using Npgsql;

namespace SharedWorlds.Backend.PostgreSql;

/// <summary>
/// Additive temporary migration schema that permanently disables Backend.Api authority and access
/// mutation for a World after peer-authority cutover begins. World/save payloads are not stored here.
/// </summary>
public static class PostgreSqlLegacySharedWorldAuthorityRetirementSchema
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS steward_legacy_authority_retirements (
            world_id uuid PRIMARY KEY REFERENCES steward_shared_worlds(world_id) ON DELETE CASCADE,
            holder_provider text NOT NULL,
            holder_external_id text NOT NULL,
            installation_id text NOT NULL,
            session_id uuid NOT NULL,
            generation bigint NOT NULL CHECK (generation > 0),
            state_revision_id uuid NOT NULL,
            environment_revision_id uuid NULL,
            active_members_fingerprint text NOT NULL
                CHECK (active_members_fingerprint ~ '^[0-9A-F]{64}$'),
            retired_at timestamptz NOT NULL
        );

        CREATE OR REPLACE FUNCTION steward_reject_retired_legacy_world_write()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        DECLARE
            target_world_id uuid;
        BEGIN
            target_world_id := CASE
                WHEN TG_OP = 'DELETE' THEN OLD.world_id
                ELSE NEW.world_id
            END;

            IF EXISTS (
                SELECT 1
                FROM steward_legacy_authority_retirements r
                WHERE r.world_id = target_world_id) THEN
                RAISE EXCEPTION 'legacy authority is permanently retired for World %', target_world_id
                    USING ERRCODE = '55000';
            END IF;

            IF TG_OP = 'DELETE' THEN
                RETURN OLD;
            END IF;

            RETURN NEW;
        END;
        $$;

        DROP TRIGGER IF EXISTS trg_steward_reject_retired_legacy_reservation_write
            ON steward_world_reservations;
        CREATE TRIGGER trg_steward_reject_retired_legacy_reservation_write
            BEFORE INSERT OR UPDATE
            ON steward_world_reservations
            FOR EACH ROW
            EXECUTE FUNCTION steward_reject_retired_legacy_world_write();

        DROP TRIGGER IF EXISTS trg_steward_reject_retired_legacy_member_write
            ON steward_world_members;
        CREATE TRIGGER trg_steward_reject_retired_legacy_member_write
            BEFORE INSERT OR UPDATE OR DELETE
            ON steward_world_members
            FOR EACH ROW
            EXECUTE FUNCTION steward_reject_retired_legacy_world_write();

        DROP TRIGGER IF EXISTS trg_steward_reject_retired_legacy_invitation_write
            ON steward_world_invitations;
        CREATE TRIGGER trg_steward_reject_retired_legacy_invitation_write
            BEFORE INSERT OR UPDATE OR DELETE
            ON steward_world_invitations
            FOR EACH ROW
            EXECUTE FUNCTION steward_reject_retired_legacy_world_write();

        DROP TRIGGER IF EXISTS trg_steward_reject_retired_legacy_world_update
            ON steward_shared_worlds;
        CREATE TRIGGER trg_steward_reject_retired_legacy_world_update
            BEFORE UPDATE OR DELETE
            ON steward_shared_worlds
            FOR EACH ROW
            EXECUTE FUNCTION steward_reject_retired_legacy_world_write();
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
