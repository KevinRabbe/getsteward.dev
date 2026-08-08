using Npgsql;

namespace SharedWorlds.Backend.PostgreSql;

/// <summary>
/// Additive temporary migration schema that permanently disables Backend.Api writable reservation
/// authority for a World after peer-authority cutover begins. World/save payloads are not stored here.
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
            retired_at timestamptz NOT NULL
        );

        CREATE OR REPLACE FUNCTION steward_reject_retired_legacy_reservation_write()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            IF EXISTS (
                SELECT 1
                FROM steward_legacy_authority_retirements r
                WHERE r.world_id = NEW.world_id) THEN
                RAISE EXCEPTION 'legacy writable authority is permanently retired for World %', NEW.world_id
                    USING ERRCODE = '55000';
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
            EXECUTE FUNCTION steward_reject_retired_legacy_reservation_write();
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
