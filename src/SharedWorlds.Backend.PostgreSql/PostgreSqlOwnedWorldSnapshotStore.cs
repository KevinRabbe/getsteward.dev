using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Backend.PostgreSql;

/// <summary>
/// PostgreSQL persistence for immutable owner-private snapshot descriptors. Publication serializes on
/// the same per-location advisory lock as location compare-and-swap and rechecks the current exact head
/// before inserting, closing the read-location/upload-finalization race at the database boundary.
/// </summary>
public sealed class PostgreSqlOwnedWorldSnapshotStore : IOwnedWorldSnapshotStore
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlOwnedWorldSnapshotStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS steward_owned_world_location_heads (
                world_id uuid NOT NULL,
                owner_provider text NOT NULL,
                owner_external_id text NOT NULL,
                installation_id text NOT NULL,
                state_revision_id uuid NOT NULL,
                environment_revision_id uuid NOT NULL,
                first_observed_at timestamptz NOT NULL,
                PRIMARY KEY (
                    world_id,
                    owner_provider,
                    owner_external_id,
                    installation_id,
                    state_revision_id,
                    environment_revision_id),
                CONSTRAINT steward_owned_world_location_heads_installation_owner_fk
                    FOREIGN KEY (installation_id, owner_provider, owner_external_id)
                    REFERENCES steward_owned_installations (
                        installation_id,
                        owner_provider,
                        owner_external_id)
                    ON DELETE CASCADE
            );

            INSERT INTO steward_owned_world_location_heads (
                world_id,
                owner_provider,
                owner_external_id,
                installation_id,
                state_revision_id,
                environment_revision_id,
                first_observed_at)
            SELECT
                world_id,
                owner_provider,
                owner_external_id,
                installation_id,
                state_revision_id,
                environment_revision_id,
                observed_at
            FROM steward_owned_world_locations
            ON CONFLICT (
                world_id,
                owner_provider,
                owner_external_id,
                installation_id,
                state_revision_id,
                environment_revision_id)
            DO UPDATE SET first_observed_at = LEAST(
                steward_owned_world_location_heads.first_observed_at,
                EXCLUDED.first_observed_at);

            CREATE OR REPLACE FUNCTION steward_record_owned_world_location_head()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                INSERT INTO steward_owned_world_location_heads (
                    world_id,
                    owner_provider,
                    owner_external_id,
                    installation_id,
                    state_revision_id,
                    environment_revision_id,
                    first_observed_at)
                VALUES (
                    NEW.world_id,
                    NEW.owner_provider,
                    NEW.owner_external_id,
                    NEW.installation_id,
                    NEW.state_revision_id,
                    NEW.environment_revision_id,
                    NEW.observed_at)
                ON CONFLICT (
                    world_id,
                    owner_provider,
                    owner_external_id,
                    installation_id,
                    state_revision_id,
                    environment_revision_id)
                DO UPDATE SET first_observed_at = LEAST(
                    steward_owned_world_location_heads.first_observed_at,
                    EXCLUDED.first_observed_at);

                RETURN NEW;
            END;
            $$;

            DROP TRIGGER IF EXISTS trg_steward_record_owned_world_location_head
                ON steward_owned_world_locations;
            CREATE TRIGGER trg_steward_record_owned_world_location_head
                AFTER INSERT OR UPDATE OF state_revision_id, environment_revision_id
                ON steward_owned_world_locations
                FOR EACH ROW
                EXECUTE FUNCTION steward_record_owned_world_location_head();

            CREATE TABLE IF NOT EXISTS steward_owned_world_snapshots (
                world_id uuid NOT NULL,
                owner_provider text NOT NULL,
                owner_external_id text NOT NULL,
                installation_id text NOT NULL,
                state_revision_id uuid NOT NULL,
                environment_revision_id uuid NOT NULL,
                game_adapter_id text NOT NULL,
                state_package_object_key text NOT NULL,
                state_package_byte_size bigint NOT NULL,
                state_package_sha256 text NOT NULL,
                environment_manifest_json jsonb NOT NULL,
                published_at timestamptz NOT NULL,
                PRIMARY KEY (
                    world_id,
                    owner_provider,
                    owner_external_id,
                    installation_id,
                    state_revision_id,
                    environment_revision_id),
                CONSTRAINT steward_owned_world_snapshots_exact_head_fk
                    FOREIGN KEY (
                        world_id,
                        owner_provider,
                        owner_external_id,
                        installation_id,
                        state_revision_id,
                        environment_revision_id)
                    REFERENCES steward_owned_world_location_heads (
                        world_id,
                        owner_provider,
                        owner_external_id,
                        installation_id,
                        state_revision_id,
                        environment_revision_id)
                    ON DELETE CASCADE,
                CONSTRAINT steward_owned_world_snapshots_game_adapter_id_length
                    CHECK (char_length(game_adapter_id) BETWEEN 1 AND 128),
                CONSTRAINT steward_owned_world_snapshots_object_key_length
                    CHECK (char_length(state_package_object_key) BETWEEN 1 AND 512),
                CONSTRAINT steward_owned_world_snapshots_byte_size
                    CHECK (state_package_byte_size BETWEEN 1 AND 21474836480),
                CONSTRAINT steward_owned_world_snapshots_sha256
                    CHECK (char_length(state_package_sha256) = 64),
                CONSTRAINT steward_owned_world_snapshots_object_key_unique
                    UNIQUE (state_package_object_key)
            );

            CREATE INDEX IF NOT EXISTS steward_owned_world_snapshots_owner_world_idx
                ON steward_owned_world_snapshots (
                    owner_provider,
                    owner_external_id,
                    world_id,
                    published_at DESC);
            """;

        await using var command = _dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<OwnedWorldSnapshot?> LoadExactAsync(
        string ownerProvider,
        string ownerExternalId,
        WorldId worldId,
        string installationId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT world_id,
                   owner_provider,
                   owner_external_id,
                   installation_id,
                   state_revision_id,
                   environment_revision_id,
                   game_adapter_id,
                   state_package_object_key,
                   state_package_byte_size,
                   state_package_sha256,
                   environment_manifest_json::text,
                   published_at
              FROM steward_owned_world_snapshots
             WHERE world_id = @world_id
               AND owner_provider = @owner_provider
               AND owner_external_id = @owner_external_id
               AND installation_id = @installation_id
               AND state_revision_id = @state_revision_id
               AND environment_revision_id = @environment_revision_id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        AddExactKeyParameters(
            command,
            ownerProvider,
            ownerExternalId,
            worldId,
            installationId,
            stateRevisionId,
            environmentRevisionId);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadSnapshot(reader)
            : null;
    }

    public async Task<IReadOnlyList<OwnedWorldSnapshot>> ListWorldSnapshotsAsync(
        string ownerProvider,
        string ownerExternalId,
        WorldId worldId,
        int maximumSnapshots,
        CancellationToken cancellationToken = default)
    {
        if (maximumSnapshots is < 1 or > BringHereSnapshotAuthorityService.MaximumSnapshotsPerWorld)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSnapshots));
        }

        const string sql = """
            SELECT world_id,
                   owner_provider,
                   owner_external_id,
                   installation_id,
                   state_revision_id,
                   environment_revision_id,
                   game_adapter_id,
                   state_package_object_key,
                   state_package_byte_size,
                   state_package_sha256,
                   environment_manifest_json::text,
                   published_at
              FROM steward_owned_world_snapshots
             WHERE world_id = @world_id
               AND owner_provider = @owner_provider
               AND owner_external_id = @owner_external_id
             ORDER BY published_at DESC,
                      installation_id ASC,
                      state_revision_id ASC,
                      environment_revision_id ASC
             LIMIT @maximum_snapshots;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", NpgsqlDbType.Uuid, worldId.Value);
        command.Parameters.AddWithValue("owner_provider", NpgsqlDbType.Text, ownerProvider);
        command.Parameters.AddWithValue("owner_external_id", NpgsqlDbType.Text, ownerExternalId);
        command.Parameters.AddWithValue("maximum_snapshots", NpgsqlDbType.Integer, maximumSnapshots);

        var snapshots = new List<OwnedWorldSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            snapshots.Add(ReadSnapshot(reader));
        }

        return snapshots;
    }

    public async Task<OwnedWorldSnapshotWriteDecision> PublishAsync(
        OwnedWorldSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var normalized = snapshot with
        {
            StatePackageSha256 = snapshot.StatePackageSha256.ToUpperInvariant(),
            PublishedAt = TruncateToMicroseconds(snapshot.PublishedAt)
        };
        normalized.Validate();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        await AcquireLocationAuthorityLockAsync(
            connection,
            transaction,
            normalized,
            cancellationToken);
        await EnsureCurrentLocationHeadAsync(
            connection,
            transaction,
            normalized,
            cancellationToken);

        var current = await LoadExactForUpdateAsync(
            connection,
            transaction,
            normalized,
            cancellationToken);
        if (current is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return SameImmutableSnapshot(current, normalized)
                ? new OwnedWorldSnapshotWriteDecision(
                    OwnedWorldSnapshotWriteResult.NoChange,
                    current,
                    "The exact private snapshot descriptor is already published.")
                : new OwnedWorldSnapshotWriteDecision(
                    OwnedWorldSnapshotWriteResult.Conflict,
                    current,
                    "The exact private snapshot key already contains different immutable metadata.");
        }

        const string sql = """
            INSERT INTO steward_owned_world_snapshots (
                world_id,
                owner_provider,
                owner_external_id,
                installation_id,
                state_revision_id,
                environment_revision_id,
                game_adapter_id,
                state_package_object_key,
                state_package_byte_size,
                state_package_sha256,
                environment_manifest_json,
                published_at)
            VALUES (
                @world_id,
                @owner_provider,
                @owner_external_id,
                @installation_id,
                @state_revision_id,
                @environment_revision_id,
                @game_adapter_id,
                @state_package_object_key,
                @state_package_byte_size,
                @state_package_sha256,
                CAST(@environment_manifest_json AS jsonb),
                @published_at);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddSnapshotParameters(command, normalized);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException(
                "The private snapshot package object key is already bound to different immutable evidence.",
                exception);
        }

        await transaction.CommitAsync(cancellationToken);
        return new OwnedWorldSnapshotWriteDecision(
            OwnedWorldSnapshotWriteResult.Created,
            normalized,
            "The exact verified private snapshot descriptor was published.");
    }

    private static async Task AcquireLocationAuthorityLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OwnedWorldSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT pg_advisory_xact_lock(
                hashtextextended(
                    concat_ws(
                        E'\x1f',
                        @world_id::text,
                        @owner_provider,
                        @owner_external_id,
                        @installation_id),
                    0));
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddLocationKeyParameters(command, snapshot);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureCurrentLocationHeadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OwnedWorldSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT state_revision_id,
                   environment_revision_id,
                   game_adapter_id
              FROM steward_owned_world_locations
             WHERE world_id = @world_id
               AND owner_provider = @owner_provider
               AND owner_external_id = @owner_external_id
               AND installation_id = @installation_id
             FOR KEY SHARE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddLocationKeyParameters(command, snapshot);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "The source installation no longer publishes this private World location.");
        }

        var currentState = new RevisionId(reader.GetGuid(0));
        var currentEnvironment = new RevisionId(reader.GetGuid(1));
        if (currentState != snapshot.StateRevisionId ||
            currentEnvironment != snapshot.EnvironmentRevisionId)
        {
            throw new InvalidOperationException(
                "The source installation changed its private World head before snapshot publication completed.");
        }

        if (!reader.IsDBNull(2) &&
            !string.Equals(reader.GetString(2), snapshot.GameAdapterId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The private snapshot adapter identity disagrees with the current location presentation.");
        }
    }

    private static async Task<OwnedWorldSnapshot?> LoadExactForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OwnedWorldSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT world_id,
                   owner_provider,
                   owner_external_id,
                   installation_id,
                   state_revision_id,
                   environment_revision_id,
                   game_adapter_id,
                   state_package_object_key,
                   state_package_byte_size,
                   state_package_sha256,
                   environment_manifest_json::text,
                   published_at
              FROM steward_owned_world_snapshots
             WHERE world_id = @world_id
               AND owner_provider = @owner_provider
               AND owner_external_id = @owner_external_id
               AND installation_id = @installation_id
               AND state_revision_id = @state_revision_id
               AND environment_revision_id = @environment_revision_id
             FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddExactKeyParameters(command, snapshot);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadSnapshot(reader)
            : null;
    }

    private static OwnedWorldSnapshot ReadSnapshot(NpgsqlDataReader reader)
    {
        var manifestJson = reader.GetString(10);
        var manifest = JsonSerializer.Deserialize<EnvironmentManifest>(
            manifestJson,
            ManifestJsonOptions)
            ?? throw new InvalidDataException(
                "Persisted private snapshot environment manifest is missing.");
        var snapshot = new OwnedWorldSnapshot(
            new WorldId(reader.GetGuid(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            new RevisionId(reader.GetGuid(4)),
            new RevisionId(reader.GetGuid(5)),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt64(8),
            reader.GetString(9),
            manifest,
            reader.GetFieldValue<DateTimeOffset>(11));
        snapshot.Validate();
        return snapshot;
    }

    private static bool SameImmutableSnapshot(
        OwnedWorldSnapshot left,
        OwnedWorldSnapshot right)
        => left.WorldId == right.WorldId &&
           string.Equals(left.OwnerProvider, right.OwnerProvider, StringComparison.Ordinal) &&
           string.Equals(left.OwnerExternalId, right.OwnerExternalId, StringComparison.Ordinal) &&
           string.Equals(left.InstallationId, right.InstallationId, StringComparison.Ordinal) &&
           left.StateRevisionId == right.StateRevisionId &&
           left.EnvironmentRevisionId == right.EnvironmentRevisionId &&
           string.Equals(left.GameAdapterId, right.GameAdapterId, StringComparison.Ordinal) &&
           string.Equals(left.StatePackageObjectKey, right.StatePackageObjectKey, StringComparison.Ordinal) &&
           left.StatePackageByteSize == right.StatePackageByteSize &&
           string.Equals(left.StatePackageSha256, right.StatePackageSha256, StringComparison.OrdinalIgnoreCase) &&
           JsonNode.DeepEquals(
               JsonSerializer.SerializeToNode(left.EnvironmentManifest, ManifestJsonOptions),
               JsonSerializer.SerializeToNode(right.EnvironmentManifest, ManifestJsonOptions)) &&
           TruncateToMicroseconds(left.PublishedAt) == TruncateToMicroseconds(right.PublishedAt);

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
        => new(value.Ticks - (value.Ticks % 10), value.Offset);

    private static void AddSnapshotParameters(
        NpgsqlCommand command,
        OwnedWorldSnapshot snapshot)
    {
        AddExactKeyParameters(command, snapshot);
        command.Parameters.AddWithValue(
            "game_adapter_id",
            NpgsqlDbType.Text,
            snapshot.GameAdapterId);
        command.Parameters.AddWithValue(
            "state_package_object_key",
            NpgsqlDbType.Text,
            snapshot.StatePackageObjectKey);
        command.Parameters.AddWithValue(
            "state_package_byte_size",
            NpgsqlDbType.Bigint,
            snapshot.StatePackageByteSize);
        command.Parameters.AddWithValue(
            "state_package_sha256",
            NpgsqlDbType.Text,
            snapshot.StatePackageSha256.ToUpperInvariant());
        command.Parameters.AddWithValue(
            "environment_manifest_json",
            NpgsqlDbType.Text,
            JsonSerializer.Serialize(snapshot.EnvironmentManifest, ManifestJsonOptions));
        command.Parameters.AddWithValue(
            "published_at",
            NpgsqlDbType.TimestampTz,
            snapshot.PublishedAt);
    }

    private static void AddExactKeyParameters(
        NpgsqlCommand command,
        OwnedWorldSnapshot snapshot)
        => AddExactKeyParameters(
            command,
            snapshot.OwnerProvider,
            snapshot.OwnerExternalId,
            snapshot.WorldId,
            snapshot.InstallationId,
            snapshot.StateRevisionId,
            snapshot.EnvironmentRevisionId);

    private static void AddExactKeyParameters(
        NpgsqlCommand command,
        string ownerProvider,
        string ownerExternalId,
        WorldId worldId,
        string installationId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId)
    {
        AddLocationKeyParameters(
            command,
            ownerProvider,
            ownerExternalId,
            worldId,
            installationId);
        command.Parameters.AddWithValue(
            "state_revision_id",
            NpgsqlDbType.Uuid,
            stateRevisionId.Value);
        command.Parameters.AddWithValue(
            "environment_revision_id",
            NpgsqlDbType.Uuid,
            environmentRevisionId.Value);
    }

    private static void AddLocationKeyParameters(
        NpgsqlCommand command,
        OwnedWorldSnapshot snapshot)
        => AddLocationKeyParameters(
            command,
            snapshot.OwnerProvider,
            snapshot.OwnerExternalId,
            snapshot.WorldId,
            snapshot.InstallationId);

    private static void AddLocationKeyParameters(
        NpgsqlCommand command,
        string ownerProvider,
        string ownerExternalId,
        WorldId worldId,
        string installationId)
    {
        command.Parameters.AddWithValue("world_id", NpgsqlDbType.Uuid, worldId.Value);
        command.Parameters.AddWithValue("owner_provider", NpgsqlDbType.Text, ownerProvider);
        command.Parameters.AddWithValue("owner_external_id", NpgsqlDbType.Text, ownerExternalId);
        command.Parameters.AddWithValue("installation_id", NpgsqlDbType.Text, installationId);
    }
}
