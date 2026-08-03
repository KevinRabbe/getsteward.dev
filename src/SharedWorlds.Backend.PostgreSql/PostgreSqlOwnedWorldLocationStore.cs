using System.Data;
using Npgsql;
using NpgsqlTypes;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Backend.PostgreSql;

/// <summary>
/// PostgreSQL authority for owned installations and private World-location claims.
/// Every mutation is owner-scoped and compare-and-swap; timestamps never select a divergent head.
/// </summary>
public sealed class PostgreSqlOwnedWorldLocationStore : IOwnedWorldLocationStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlOwnedWorldLocationStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS steward_owned_installations (
                installation_id text PRIMARY KEY,
                owner_provider text NOT NULL,
                owner_external_id text NOT NULL,
                display_name text NOT NULL,
                registered_at timestamptz NOT NULL,
                last_seen_at timestamptz NOT NULL,
                CONSTRAINT steward_owned_installations_installation_id_length
                    CHECK (char_length(installation_id) BETWEEN 1 AND 128),
                CONSTRAINT steward_owned_installations_owner_provider_length
                    CHECK (char_length(owner_provider) BETWEEN 1 AND 128),
                CONSTRAINT steward_owned_installations_owner_external_id_length
                    CHECK (char_length(owner_external_id) BETWEEN 1 AND 256),
                CONSTRAINT steward_owned_installations_display_name_length
                    CHECK (char_length(display_name) BETWEEN 1 AND 80),
                CONSTRAINT steward_owned_installations_owner_key
                    UNIQUE (installation_id, owner_provider, owner_external_id)
            );

            CREATE TABLE IF NOT EXISTS steward_owned_world_locations (
                world_id uuid NOT NULL,
                owner_provider text NOT NULL,
                owner_external_id text NOT NULL,
                installation_id text NOT NULL,
                state_revision_id uuid NOT NULL,
                environment_revision_id uuid NOT NULL,
                observed_at timestamptz NOT NULL,
                PRIMARY KEY (world_id, owner_provider, owner_external_id, installation_id),
                CONSTRAINT steward_owned_world_locations_installation_owner_fk
                    FOREIGN KEY (installation_id, owner_provider, owner_external_id)
                    REFERENCES steward_owned_installations (
                        installation_id,
                        owner_provider,
                        owner_external_id)
                    ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS steward_owned_world_locations_owner_world_idx
                ON steward_owned_world_locations (owner_provider, owner_external_id, world_id);
            """;

        await using var command = _dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<OwnedInstallationRegistration?> GetInstallationAsync(
        string installationId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT installation_id,
                   owner_provider,
                   owner_external_id,
                   display_name,
                   registered_at,
                   last_seen_at
              FROM steward_owned_installations
             WHERE installation_id = @installation_id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("installation_id", NpgsqlDbType.Text, installationId);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadInstallation(reader)
            : null;
    }

    public async Task RegisterInstallationAsync(
        OwnedInstallationRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        const string sql = """
            INSERT INTO steward_owned_installations (
                installation_id,
                owner_provider,
                owner_external_id,
                display_name,
                registered_at,
                last_seen_at)
            VALUES (
                @installation_id,
                @owner_provider,
                @owner_external_id,
                @display_name,
                @registered_at,
                @last_seen_at)
            ON CONFLICT (installation_id) DO UPDATE
               SET display_name = EXCLUDED.display_name,
                   last_seen_at = GREATEST(
                       steward_owned_installations.last_seen_at,
                       EXCLUDED.last_seen_at)
             WHERE steward_owned_installations.owner_provider = EXCLUDED.owner_provider
               AND steward_owned_installations.owner_external_id = EXCLUDED.owner_external_id
            RETURNING installation_id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        AddInstallationParameters(command, registration);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null)
        {
            throw new InvalidOperationException(
                "This installation ID is already registered to another owner.");
        }
    }

    public async Task<IReadOnlyList<OwnedWorldLocationClaim>> ListWorldLocationsAsync(
        WorldId worldId,
        string ownerProvider,
        string ownerExternalId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT world_id,
                   owner_provider,
                   owner_external_id,
                   installation_id,
                   state_revision_id,
                   environment_revision_id,
                   observed_at
              FROM steward_owned_world_locations
             WHERE world_id = @world_id
               AND owner_provider = @owner_provider
               AND owner_external_id = @owner_external_id
             ORDER BY observed_at DESC, installation_id ASC;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", NpgsqlDbType.Uuid, worldId.Value);
        command.Parameters.AddWithValue("owner_provider", NpgsqlDbType.Text, ownerProvider);
        command.Parameters.AddWithValue("owner_external_id", NpgsqlDbType.Text, ownerExternalId);

        var claims = new List<OwnedWorldLocationClaim>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claims.Add(ReadLocation(reader));
        }

        return claims;
    }

    public async Task<OwnedWorldLocationWriteDecision> CompareExchangeLocationAsync(
        OwnedWorldLocationClaim desired,
        RevisionId? expectedStateRevisionId,
        RevisionId? expectedEnvironmentRevisionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(desired);
        EnsureExpectedPair(expectedStateRevisionId, expectedEnvironmentRevisionId);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        await EnsureInstallationOwnerAsync(
            connection,
            transaction,
            desired.InstallationId,
            desired.OwnerProvider,
            desired.OwnerExternalId,
            cancellationToken);

        var current = await GetLocationForUpdateAsync(
            connection,
            transaction,
            desired.WorldId,
            desired.OwnerProvider,
            desired.OwnerExternalId,
            desired.InstallationId,
            cancellationToken);

        if (current is null)
        {
            if (expectedStateRevisionId is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Conflict(null, "The expected prior location no longer exists.");
            }

            await InsertLocationAsync(connection, transaction, desired, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(
                OwnedWorldLocationWriteResult.Created,
                desired,
                "The installation's first private World location was published.");
        }

        if (SameHead(current, desired))
        {
            if (desired.ObservedAt > current.ObservedAt)
            {
                current = current with { ObservedAt = desired.ObservedAt };
                await UpdateObservedAtAsync(
                    connection,
                    transaction,
                    current,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new(
                OwnedWorldLocationWriteResult.NoChange,
                current,
                "The installation already reports the same immutable state/environment head.");
        }

        if (!ExpectedMatches(current, expectedStateRevisionId, expectedEnvironmentRevisionId))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict(
                current,
                "The stored World location changed since it was observed. Newer timestamps cannot overwrite a divergent head.");
        }

        await UpdateLocationAsync(connection, transaction, desired, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(
            OwnedWorldLocationWriteResult.Updated,
            desired,
            "The exact previously observed World location was replaced atomically.");
    }

    public async Task<OwnedWorldLocationWriteDecision> RemoveLocationAsync(
        WorldId worldId,
        string ownerProvider,
        string ownerExternalId,
        string installationId,
        RevisionId expectedStateRevisionId,
        RevisionId expectedEnvironmentRevisionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        await EnsureInstallationOwnerAsync(
            connection,
            transaction,
            installationId,
            ownerProvider,
            ownerExternalId,
            cancellationToken);

        var current = await GetLocationForUpdateAsync(
            connection,
            transaction,
            worldId,
            ownerProvider,
            ownerExternalId,
            installationId,
            cancellationToken);
        if (current is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(
                OwnedWorldLocationWriteResult.NoChange,
                Current: null,
                "The private World location is already absent.");
        }

        if (current.StateRevisionId != expectedStateRevisionId ||
            current.EnvironmentRevisionId != expectedEnvironmentRevisionId)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict(
                current,
                "The private World location changed before removal and was preserved.");
        }

        const string sql = """
            DELETE FROM steward_owned_world_locations
             WHERE world_id = @world_id
               AND owner_provider = @owner_provider
               AND owner_external_id = @owner_external_id
               AND installation_id = @installation_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddLocationKeyParameters(
            command,
            worldId,
            ownerProvider,
            ownerExternalId,
            installationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(
            OwnedWorldLocationWriteResult.Updated,
            Current: null,
            "The exact private World location was removed.");
    }

    private static async Task EnsureInstallationOwnerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string installationId,
        string ownerProvider,
        string ownerExternalId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT owner_provider, owner_external_id
              FROM steward_owned_installations
             WHERE installation_id = @installation_id
             FOR KEY SHARE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("installation_id", NpgsqlDbType.Text, installationId);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "The installation must be registered before publishing a World location.");
        }

        if (!string.Equals(reader.GetString(0), ownerProvider, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), ownerExternalId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The installation belongs to a different authenticated owner.");
        }
    }

    private static async Task<OwnedWorldLocationClaim?> GetLocationForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        string ownerProvider,
        string ownerExternalId,
        string installationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT world_id,
                   owner_provider,
                   owner_external_id,
                   installation_id,
                   state_revision_id,
                   environment_revision_id,
                   observed_at
              FROM steward_owned_world_locations
             WHERE world_id = @world_id
               AND owner_provider = @owner_provider
               AND owner_external_id = @owner_external_id
               AND installation_id = @installation_id
             FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddLocationKeyParameters(
            command,
            worldId,
            ownerProvider,
            ownerExternalId,
            installationId);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadLocation(reader)
            : null;
    }

    private static async Task InsertLocationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OwnedWorldLocationClaim location,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO steward_owned_world_locations (
                world_id,
                owner_provider,
                owner_external_id,
                installation_id,
                state_revision_id,
                environment_revision_id,
                observed_at)
            VALUES (
                @world_id,
                @owner_provider,
                @owner_external_id,
                @installation_id,
                @state_revision_id,
                @environment_revision_id,
                @observed_at);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddLocationParameters(command, location);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateLocationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OwnedWorldLocationClaim location,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE steward_owned_world_locations
               SET state_revision_id = @state_revision_id,
                   environment_revision_id = @environment_revision_id,
                   observed_at = @observed_at
             WHERE world_id = @world_id
               AND owner_provider = @owner_provider
               AND owner_external_id = @owner_external_id
               AND installation_id = @installation_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddLocationParameters(command, location);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateObservedAtAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OwnedWorldLocationClaim location,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE steward_owned_world_locations
               SET observed_at = @observed_at
             WHERE world_id = @world_id
               AND owner_provider = @owner_provider
               AND owner_external_id = @owner_external_id
               AND installation_id = @installation_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddLocationKeyParameters(
            command,
            location.WorldId,
            location.OwnerProvider,
            location.OwnerExternalId,
            location.InstallationId);
        command.Parameters.AddWithValue(
            "observed_at",
            NpgsqlDbType.TimestampTz,
            location.ObservedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static OwnedInstallationRegistration ReadInstallation(NpgsqlDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5));

    private static OwnedWorldLocationClaim ReadLocation(NpgsqlDataReader reader)
        => new(
            new WorldId(reader.GetGuid(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            new RevisionId(reader.GetGuid(4)),
            new RevisionId(reader.GetGuid(5)),
            reader.GetFieldValue<DateTimeOffset>(6));

    private static void AddInstallationParameters(
        NpgsqlCommand command,
        OwnedInstallationRegistration registration)
    {
        command.Parameters.AddWithValue(
            "installation_id",
            NpgsqlDbType.Text,
            registration.InstallationId);
        command.Parameters.AddWithValue(
            "owner_provider",
            NpgsqlDbType.Text,
            registration.OwnerProvider);
        command.Parameters.AddWithValue(
            "owner_external_id",
            NpgsqlDbType.Text,
            registration.OwnerExternalId);
        command.Parameters.AddWithValue(
            "display_name",
            NpgsqlDbType.Text,
            registration.DisplayName);
        command.Parameters.AddWithValue(
            "registered_at",
            NpgsqlDbType.TimestampTz,
            registration.RegisteredAt);
        command.Parameters.AddWithValue(
            "last_seen_at",
            NpgsqlDbType.TimestampTz,
            registration.LastSeenAt);
    }

    private static void AddLocationParameters(
        NpgsqlCommand command,
        OwnedWorldLocationClaim location)
    {
        AddLocationKeyParameters(
            command,
            location.WorldId,
            location.OwnerProvider,
            location.OwnerExternalId,
            location.InstallationId);
        command.Parameters.AddWithValue(
            "state_revision_id",
            NpgsqlDbType.Uuid,
            location.StateRevisionId.Value);
        command.Parameters.AddWithValue(
            "environment_revision_id",
            NpgsqlDbType.Uuid,
            location.EnvironmentRevisionId.Value);
        command.Parameters.AddWithValue(
            "observed_at",
            NpgsqlDbType.TimestampTz,
            location.ObservedAt);
    }

    private static void AddLocationKeyParameters(
        NpgsqlCommand command,
        WorldId worldId,
        string ownerProvider,
        string ownerExternalId,
        string installationId)
    {
        command.Parameters.AddWithValue("world_id", NpgsqlDbType.Uuid, worldId.Value);
        command.Parameters.AddWithValue("owner_provider", NpgsqlDbType.Text, ownerProvider);
        command.Parameters.AddWithValue("owner_external_id", NpgsqlDbType.Text, ownerExternalId);
        command.Parameters.AddWithValue("installation_id", NpgsqlDbType.Text, installationId);
    }

    private static void EnsureExpectedPair(
        RevisionId? expectedStateRevisionId,
        RevisionId? expectedEnvironmentRevisionId)
    {
        if (expectedStateRevisionId.HasValue != expectedEnvironmentRevisionId.HasValue)
        {
            throw new ArgumentException(
                "Expected state and environment revisions must both be supplied or both be absent.");
        }
    }

    private static bool ExpectedMatches(
        OwnedWorldLocationClaim current,
        RevisionId? expectedStateRevisionId,
        RevisionId? expectedEnvironmentRevisionId)
        => expectedStateRevisionId is not null &&
           expectedEnvironmentRevisionId is not null &&
           current.StateRevisionId == expectedStateRevisionId.Value &&
           current.EnvironmentRevisionId == expectedEnvironmentRevisionId.Value;

    private static bool SameHead(
        OwnedWorldLocationClaim left,
        OwnedWorldLocationClaim right)
        => left.StateRevisionId == right.StateRevisionId &&
           left.EnvironmentRevisionId == right.EnvironmentRevisionId;

    private static OwnedWorldLocationWriteDecision Conflict(
        OwnedWorldLocationClaim? current,
        string reason)
        => new(OwnedWorldLocationWriteResult.Conflict, current, reason);
}
