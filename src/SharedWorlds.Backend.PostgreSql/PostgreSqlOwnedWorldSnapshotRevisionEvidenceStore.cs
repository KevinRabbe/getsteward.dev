using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Backend.PostgreSql;

/// <summary>
/// Additive immutable persistence for the exact revision records behind verified owner-private
/// snapshot bytes. Legacy snapshot descriptors remain byte-ready but have no evidence row and are
/// therefore non-materializable until the source explicitly publishes exact records.
/// </summary>
public sealed class PostgreSqlOwnedWorldSnapshotRevisionEvidenceStore :
    IOwnedWorldSnapshotRevisionEvidenceStore
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlOwnedWorldSnapshotRevisionEvidenceStore(
        NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS steward_owned_world_snapshot_revision_evidence (
                world_id uuid NOT NULL,
                owner_provider text NOT NULL,
                owner_external_id text NOT NULL,
                installation_id text NOT NULL,
                state_revision_id uuid NOT NULL,
                environment_revision_id uuid NOT NULL,
                state_revision_json jsonb NOT NULL,
                environment_revision_json jsonb NOT NULL,
                recorded_at timestamptz NOT NULL,
                PRIMARY KEY (
                    world_id,
                    owner_provider,
                    owner_external_id,
                    installation_id,
                    state_revision_id,
                    environment_revision_id),
                CONSTRAINT steward_owned_world_snapshot_revision_evidence_snapshot_fk
                    FOREIGN KEY (
                        world_id,
                        owner_provider,
                        owner_external_id,
                        installation_id,
                        state_revision_id,
                        environment_revision_id)
                    REFERENCES steward_owned_world_snapshots (
                        world_id,
                        owner_provider,
                        owner_external_id,
                        installation_id,
                        state_revision_id,
                        environment_revision_id)
                    ON DELETE CASCADE,
                CONSTRAINT steward_owned_world_snapshot_revision_evidence_state_object
                    CHECK (jsonb_typeof(state_revision_json) = 'object'),
                CONSTRAINT steward_owned_world_snapshot_revision_evidence_environment_object
                    CHECK (jsonb_typeof(environment_revision_json) = 'object')
            );

            CREATE INDEX IF NOT EXISTS steward_owned_world_snapshot_revision_evidence_owner_world_idx
                ON steward_owned_world_snapshot_revision_evidence (
                    owner_provider,
                    owner_external_id,
                    world_id,
                    recorded_at DESC);
            """;

        await using var command = _dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<OwnedWorldSnapshotRevisionEvidence?> LoadExactAsync(
        string ownerProvider,
        string ownerExternalId,
        WorldId worldId,
        string installationId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT owner_provider,
                   owner_external_id,
                   installation_id,
                   world_id,
                   state_revision_json::text,
                   environment_revision_json::text,
                   recorded_at
              FROM steward_owned_world_snapshot_revision_evidence
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
            ? ReadEvidence(reader)
            : null;
    }

    public async Task<IReadOnlyList<OwnedWorldSnapshotRevisionEvidence>>
        ListWorldEvidenceAsync(
            string ownerProvider,
            string ownerExternalId,
            WorldId worldId,
            int maximumEvidence,
            CancellationToken cancellationToken = default)
    {
        if (maximumEvidence is < 1 or >
            BringHereMaterializationAuthorityService.MaximumRevisionEvidencePerWorld)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEvidence));
        }

        const string sql = """
            SELECT owner_provider,
                   owner_external_id,
                   installation_id,
                   world_id,
                   state_revision_json::text,
                   environment_revision_json::text,
                   recorded_at
              FROM steward_owned_world_snapshot_revision_evidence
             WHERE world_id = @world_id
               AND owner_provider = @owner_provider
               AND owner_external_id = @owner_external_id
             ORDER BY recorded_at DESC,
                      installation_id ASC,
                      state_revision_id ASC,
                      environment_revision_id ASC
             LIMIT @maximum_evidence;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(
            "world_id",
            NpgsqlDbType.Uuid,
            worldId.Value);
        command.Parameters.AddWithValue(
            "owner_provider",
            NpgsqlDbType.Text,
            ownerProvider);
        command.Parameters.AddWithValue(
            "owner_external_id",
            NpgsqlDbType.Text,
            ownerExternalId);
        command.Parameters.AddWithValue(
            "maximum_evidence",
            NpgsqlDbType.Integer,
            maximumEvidence);

        var evidence = new List<OwnedWorldSnapshotRevisionEvidence>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            evidence.Add(ReadEvidence(reader));
        }

        return evidence;
    }

    public async Task<OwnedWorldSnapshotRevisionEvidenceWriteDecision> PublishAsync(
        OwnedWorldSnapshotRevisionEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var normalized = evidence with
        {
            RecordedAt = TruncateToMicroseconds(evidence.RecordedAt)
        };
        normalized.Validate();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        await AcquireExactKeyLockAsync(
            connection,
            transaction,
            normalized,
            cancellationToken);
        await EnsureSnapshotExistsAsync(
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
            return SameImmutableEvidence(current, normalized)
                ? new OwnedWorldSnapshotRevisionEvidenceWriteDecision(
                    OwnedWorldSnapshotRevisionEvidenceWriteResult.NoChange,
                    current,
                    "The exact private revision evidence is already published.")
                : new OwnedWorldSnapshotRevisionEvidenceWriteDecision(
                    OwnedWorldSnapshotRevisionEvidenceWriteResult.Conflict,
                    current,
                    "The exact private revision-evidence key already contains different immutable records.");
        }

        const string sql = """
            INSERT INTO steward_owned_world_snapshot_revision_evidence (
                world_id,
                owner_provider,
                owner_external_id,
                installation_id,
                state_revision_id,
                environment_revision_id,
                state_revision_json,
                environment_revision_json,
                recorded_at)
            VALUES (
                @world_id,
                @owner_provider,
                @owner_external_id,
                @installation_id,
                @state_revision_id,
                @environment_revision_id,
                CAST(@state_revision_json AS jsonb),
                CAST(@environment_revision_json AS jsonb),
                @recorded_at);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddEvidenceParameters(command, normalized);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new OwnedWorldSnapshotRevisionEvidenceWriteDecision(
            OwnedWorldSnapshotRevisionEvidenceWriteResult.Created,
            normalized,
            "The exact private revision records were published.");
    }

    private static async Task AcquireExactKeyLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OwnedWorldSnapshotRevisionEvidence evidence,
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
                        @installation_id,
                        @state_revision_id::text,
                        @environment_revision_id::text),
                    0));
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddExactKeyParameters(command, evidence);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSnapshotExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OwnedWorldSnapshotRevisionEvidence evidence,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1
              FROM steward_owned_world_snapshots
             WHERE world_id = @world_id
               AND owner_provider = @owner_provider
               AND owner_external_id = @owner_external_id
               AND installation_id = @installation_id
               AND state_revision_id = @state_revision_id
               AND environment_revision_id = @environment_revision_id
             FOR KEY SHARE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddExactKeyParameters(command, evidence);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null)
        {
            throw new InvalidOperationException(
                "Verified private snapshot bytes must exist before exact revision evidence can be persisted.");
        }
    }

    private static async Task<OwnedWorldSnapshotRevisionEvidence?>
        LoadExactForUpdateAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            OwnedWorldSnapshotRevisionEvidence evidence,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT owner_provider,
                   owner_external_id,
                   installation_id,
                   world_id,
                   state_revision_json::text,
                   environment_revision_json::text,
                   recorded_at
              FROM steward_owned_world_snapshot_revision_evidence
             WHERE world_id = @world_id
               AND owner_provider = @owner_provider
               AND owner_external_id = @owner_external_id
               AND installation_id = @installation_id
               AND state_revision_id = @state_revision_id
               AND environment_revision_id = @environment_revision_id
             FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddExactKeyParameters(command, evidence);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadEvidence(reader)
            : null;
    }

    private static OwnedWorldSnapshotRevisionEvidence ReadEvidence(
        NpgsqlDataReader reader)
    {
        StateRevision state;
        EnvironmentRevision environment;
        try
        {
            state = JsonSerializer.Deserialize<StateRevision>(
                        reader.GetString(4),
                        JsonOptions)
                    ?? throw new InvalidDataException(
                        "Persisted private state revision evidence is missing.");
            environment = JsonSerializer.Deserialize<EnvironmentRevision>(
                              reader.GetString(5),
                              JsonOptions)
                          ?? throw new InvalidDataException(
                              "Persisted private environment revision evidence is missing.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Persisted private revision evidence contains invalid JSON.",
                exception);
        }

        var evidence = new OwnedWorldSnapshotRevisionEvidence(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            new WorldId(reader.GetGuid(3)),
            state,
            environment,
            reader.GetFieldValue<DateTimeOffset>(6));
        evidence.Validate();
        return evidence;
    }

    private static bool SameImmutableEvidence(
        OwnedWorldSnapshotRevisionEvidence left,
        OwnedWorldSnapshotRevisionEvidence right)
        => string.Equals(left.OwnerProvider, right.OwnerProvider, StringComparison.Ordinal) &&
           string.Equals(left.OwnerExternalId, right.OwnerExternalId, StringComparison.Ordinal) &&
           string.Equals(left.InstallationId, right.InstallationId, StringComparison.Ordinal) &&
           left.WorldId == right.WorldId &&
           JsonNode.DeepEquals(
               JsonSerializer.SerializeToNode(left.StateRevision, JsonOptions),
               JsonSerializer.SerializeToNode(right.StateRevision, JsonOptions)) &&
           JsonNode.DeepEquals(
               JsonSerializer.SerializeToNode(left.EnvironmentRevision, JsonOptions),
               JsonSerializer.SerializeToNode(right.EnvironmentRevision, JsonOptions)) &&
           TruncateToMicroseconds(left.RecordedAt) ==
           TruncateToMicroseconds(right.RecordedAt);

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
        => new(value.Ticks - (value.Ticks % 10), value.Offset);

    private static void AddEvidenceParameters(
        NpgsqlCommand command,
        OwnedWorldSnapshotRevisionEvidence evidence)
    {
        AddExactKeyParameters(command, evidence);
        command.Parameters.AddWithValue(
            "state_revision_json",
            NpgsqlDbType.Text,
            JsonSerializer.Serialize(evidence.StateRevision, JsonOptions));
        command.Parameters.AddWithValue(
            "environment_revision_json",
            NpgsqlDbType.Text,
            JsonSerializer.Serialize(evidence.EnvironmentRevision, JsonOptions));
        command.Parameters.AddWithValue(
            "recorded_at",
            NpgsqlDbType.TimestampTz,
            evidence.RecordedAt);
    }

    private static void AddExactKeyParameters(
        NpgsqlCommand command,
        OwnedWorldSnapshotRevisionEvidence evidence)
        => AddExactKeyParameters(
            command,
            evidence.OwnerProvider,
            evidence.OwnerExternalId,
            evidence.WorldId,
            evidence.InstallationId,
            evidence.StateRevision.Id,
            evidence.EnvironmentRevision.Id);

    private static void AddExactKeyParameters(
        NpgsqlCommand command,
        string ownerProvider,
        string ownerExternalId,
        WorldId worldId,
        string installationId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId)
    {
        command.Parameters.AddWithValue(
            "world_id",
            NpgsqlDbType.Uuid,
            worldId.Value);
        command.Parameters.AddWithValue(
            "owner_provider",
            NpgsqlDbType.Text,
            ownerProvider);
        command.Parameters.AddWithValue(
            "owner_external_id",
            NpgsqlDbType.Text,
            ownerExternalId);
        command.Parameters.AddWithValue(
            "installation_id",
            NpgsqlDbType.Text,
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
}
