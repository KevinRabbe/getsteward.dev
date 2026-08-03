using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Environment;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Backend.PostgreSql;

/// <summary>
/// Durable private snapshot multipart intent. The table is separate from shared package transfers and
/// binds every transfer to the authenticated owner, source installation, and exact historical location
/// head. State changes use compare-and-swap predicates rather than last-writer-wins updates.
/// </summary>
public sealed class PostgreSqlPrivateSnapshotTransferStore : IPrivateSnapshotTransferStore
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlPrivateSnapshotTransferStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS steward_private_snapshot_transfers (
                transfer_id uuid PRIMARY KEY,
                owner_provider text NOT NULL,
                owner_external_id text NOT NULL,
                source_installation_id text NOT NULL,
                world_id uuid NOT NULL,
                state_revision_id uuid NOT NULL,
                environment_revision_id uuid NOT NULL,
                game_adapter_id text NOT NULL,
                object_key text NOT NULL UNIQUE,
                provider_upload_id text NOT NULL UNIQUE,
                expected_byte_size bigint NOT NULL,
                expected_sha256 text NOT NULL,
                environment_manifest_json jsonb NOT NULL,
                part_size_bytes integer NOT NULL,
                part_count integer NOT NULL,
                created_at timestamptz NOT NULL,
                expires_at timestamptz NOT NULL,
                state text NOT NULL,
                state_changed_at timestamptz NULL,
                CONSTRAINT steward_private_snapshot_transfers_exact_head_fk
                    FOREIGN KEY (
                        world_id,
                        owner_provider,
                        owner_external_id,
                        source_installation_id,
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
                CONSTRAINT steward_private_snapshot_transfers_adapter_length
                    CHECK (char_length(game_adapter_id) BETWEEN 1 AND 128),
                CONSTRAINT steward_private_snapshot_transfers_object_key_length
                    CHECK (char_length(object_key) BETWEEN 1 AND 512),
                CONSTRAINT steward_private_snapshot_transfers_provider_id_length
                    CHECK (char_length(provider_upload_id) BETWEEN 1 AND 512),
                CONSTRAINT steward_private_snapshot_transfers_expected_size
                    CHECK (expected_byte_size BETWEEN 1 AND 21474836480),
                CONSTRAINT steward_private_snapshot_transfers_expected_sha
                    CHECK (char_length(expected_sha256) = 64),
                CONSTRAINT steward_private_snapshot_transfers_part_size
                    CHECK (part_size_bytes > 0),
                CONSTRAINT steward_private_snapshot_transfers_part_count
                    CHECK (
                        part_count > 0 AND
                        part_count = ((expected_byte_size + part_size_bytes - 1) / part_size_bytes)),
                CONSTRAINT steward_private_snapshot_transfers_expiry
                    CHECK (expires_at > created_at),
                CONSTRAINT steward_private_snapshot_transfers_state
                    CHECK (state IN (
                        'Provisioning',
                        'Active',
                        'IntegrityFailed',
                        'PublicationBlocked',
                        'SnapshotConflict',
                        'Finalized')),
                CONSTRAINT steward_private_snapshot_transfers_state_time
                    CHECK (
                        (state IN ('Provisioning', 'Active') AND state_changed_at IS NULL) OR
                        (state IN (
                            'IntegrityFailed',
                            'PublicationBlocked',
                            'SnapshotConflict',
                            'Finalized') AND state_changed_at IS NOT NULL))
            );

            CREATE INDEX IF NOT EXISTS steward_private_snapshot_transfers_owner_idx
                ON steward_private_snapshot_transfers (
                    owner_provider,
                    owner_external_id,
                    source_installation_id,
                    created_at DESC);

            CREATE INDEX IF NOT EXISTS steward_private_snapshot_transfers_expiry_idx
                ON steward_private_snapshot_transfers (expires_at)
                WHERE state IN ('Provisioning', 'Active');
            """;

        await using var command = _dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> TryCreateAsync(
        PrivateSnapshotTransferRecord transfer,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeAndValidateNewTransfer(transfer);
        const string sql = """
            INSERT INTO steward_private_snapshot_transfers (
                transfer_id,
                owner_provider,
                owner_external_id,
                source_installation_id,
                world_id,
                state_revision_id,
                environment_revision_id,
                game_adapter_id,
                object_key,
                provider_upload_id,
                expected_byte_size,
                expected_sha256,
                environment_manifest_json,
                part_size_bytes,
                part_count,
                created_at,
                expires_at,
                state,
                state_changed_at)
            VALUES (
                @transfer_id,
                @owner_provider,
                @owner_external_id,
                @source_installation_id,
                @world_id,
                @state_revision_id,
                @environment_revision_id,
                @game_adapter_id,
                @object_key,
                @provider_upload_id,
                @expected_byte_size,
                @expected_sha256,
                CAST(@environment_manifest_json AS jsonb),
                @part_size_bytes,
                @part_count,
                @created_at,
                @expires_at,
                @state,
                NULL)
            ON CONFLICT DO NOTHING;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        AddTransferParameters(command, normalized);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<PrivateSnapshotTransferRecord?> LoadAsync(
        PrivateSnapshotTransferId transferId,
        CancellationToken cancellationToken = default)
    {
        ValidateTransferId(transferId);
        const string sql = """
            SELECT transfer_id,
                   owner_provider,
                   owner_external_id,
                   source_installation_id,
                   world_id,
                   state_revision_id,
                   environment_revision_id,
                   game_adapter_id,
                   object_key,
                   provider_upload_id,
                   expected_byte_size,
                   expected_sha256,
                   environment_manifest_json::text,
                   part_size_bytes,
                   part_count,
                   created_at,
                   expires_at,
                   state,
                   state_changed_at
              FROM steward_private_snapshot_transfers
             WHERE transfer_id = @transfer_id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("transfer_id", NpgsqlDbType.Uuid, transferId.Value);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadTransfer(reader)
            : null;
    }

    public async Task<PrivateSnapshotTransferRecord?> LoadInFlightByObjectKeyAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ValidateText(objectKey, "Private snapshot object key", 512);
        const string sql = """
            SELECT transfer_id,
                   owner_provider,
                   owner_external_id,
                   source_installation_id,
                   world_id,
                   state_revision_id,
                   environment_revision_id,
                   game_adapter_id,
                   object_key,
                   provider_upload_id,
                   expected_byte_size,
                   expected_sha256,
                   environment_manifest_json::text,
                   part_size_bytes,
                   part_count,
                   created_at,
                   expires_at,
                   state,
                   state_changed_at
              FROM steward_private_snapshot_transfers
             WHERE object_key = @object_key
               AND state IN ('Provisioning', 'Active');
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("object_key", NpgsqlDbType.Text, objectKey);
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadTransfer(reader)
            : null;
    }

    public async Task<bool> TryActivateProvisioningAsync(
        PrivateSnapshotTransferId transferId,
        string ownerProvider,
        string ownerExternalId,
        string sourceInstallationId,
        string expectedPlaceholderProviderUploadId,
        string providerUploadId,
        CancellationToken cancellationToken = default)
    {
        ValidateTransferId(transferId);
        ValidateOwnerAndInstallation(ownerProvider, ownerExternalId, sourceInstallationId);
        ValidateText(
            expectedPlaceholderProviderUploadId,
            "Expected provider-upload placeholder",
            512);
        ValidateText(providerUploadId, "Provider upload ID", 512);
        if (string.Equals(
                expectedPlaceholderProviderUploadId,
                providerUploadId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Provider upload ID must replace the provisioning placeholder.",
                nameof(providerUploadId));
        }

        const string sql = """
            UPDATE steward_private_snapshot_transfers
               SET provider_upload_id = @provider_upload_id,
                   state = 'Active'
             WHERE transfer_id = @transfer_id
               AND owner_provider = @owner_provider
               AND owner_external_id = @owner_external_id
               AND source_installation_id = @source_installation_id
               AND state = 'Provisioning'
               AND provider_upload_id = @expected_provider_upload_id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        AddOwnerTransferParameters(
            command,
            transferId,
            ownerProvider,
            ownerExternalId,
            sourceInstallationId);
        command.Parameters.AddWithValue(
            "expected_provider_upload_id",
            NpgsqlDbType.Text,
            expectedPlaceholderProviderUploadId);
        command.Parameters.AddWithValue(
            "provider_upload_id",
            NpgsqlDbType.Text,
            providerUploadId);
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    public async Task<bool> TrySetStateAsync(
        PrivateSnapshotTransferId transferId,
        string ownerProvider,
        string ownerExternalId,
        string sourceInstallationId,
        PrivateSnapshotTransferState expectedState,
        PrivateSnapshotTransferState nextState,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateTransferId(transferId);
        ValidateOwnerAndInstallation(ownerProvider, ownerExternalId, sourceInstallationId);
        if (expectedState != PrivateSnapshotTransferState.Active ||
            nextState is PrivateSnapshotTransferState.Provisioning or
                PrivateSnapshotTransferState.Active)
        {
            throw new ArgumentException(
                "Private transfer terminal state changes must compare from Active to a terminal state.");
        }

        if (changedAt == default)
        {
            throw new ArgumentException("Private transfer state-change time is required.", nameof(changedAt));
        }

        const string sql = """
            UPDATE steward_private_snapshot_transfers
               SET state = @next_state,
                   state_changed_at = @state_changed_at
             WHERE transfer_id = @transfer_id
               AND owner_provider = @owner_provider
               AND owner_external_id = @owner_external_id
               AND source_installation_id = @source_installation_id
               AND state = @expected_state;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        AddOwnerTransferParameters(
            command,
            transferId,
            ownerProvider,
            ownerExternalId,
            sourceInstallationId);
        command.Parameters.AddWithValue(
            "expected_state",
            NpgsqlDbType.Text,
            expectedState.ToString());
        command.Parameters.AddWithValue(
            "next_state",
            NpgsqlDbType.Text,
            nextState.ToString());
        command.Parameters.AddWithValue(
            "state_changed_at",
            NpgsqlDbType.TimestampTz,
            TruncateToMicroseconds(changedAt));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static PrivateSnapshotTransferRecord NormalizeAndValidateNewTransfer(
        PrivateSnapshotTransferRecord transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        ValidateTransferId(transfer.Id);
        if (transfer.State != PrivateSnapshotTransferState.Provisioning ||
            transfer.StateChangedAt is not null)
        {
            throw new InvalidDataException(
                "A newly persisted private transfer must be in Provisioning with no state-change time.");
        }

        var expectedPlaceholder = $"pending:{transfer.Id.Value:N}";
        if (!string.Equals(
                transfer.ProviderUploadId,
                expectedPlaceholder,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "A newly persisted private transfer must contain its exact provisioning placeholder.");
        }

        var normalized = transfer with
        {
            ExpectedSha256 = transfer.ExpectedSha256.ToUpperInvariant(),
            CreatedAt = TruncateToMicroseconds(transfer.CreatedAt),
            ExpiresAt = TruncateToMicroseconds(transfer.ExpiresAt)
        };
        ValidateTransfer(normalized);
        return normalized;
    }

    private static PrivateSnapshotTransferRecord ReadTransfer(NpgsqlDataReader reader)
    {
        var manifest = JsonSerializer.Deserialize<EnvironmentManifest>(
            reader.GetString(12),
            ManifestJsonOptions)
            ?? throw new InvalidDataException(
                "Persisted private transfer environment manifest is missing.");
        var stateText = reader.GetString(17);
        if (!Enum.TryParse<PrivateSnapshotTransferState>(
                stateText,
                ignoreCase: false,
                out var state) ||
            !Enum.IsDefined(state))
        {
            throw new InvalidDataException(
                "Persisted private transfer state is invalid.");
        }

        var transfer = new PrivateSnapshotTransferRecord(
            new PrivateSnapshotTransferId(reader.GetGuid(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            new WorldId(reader.GetGuid(4)),
            new RevisionId(reader.GetGuid(5)),
            new RevisionId(reader.GetGuid(6)),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetInt64(10),
            reader.GetString(11),
            manifest,
            reader.GetInt32(13),
            reader.GetInt32(14),
            reader.GetFieldValue<DateTimeOffset>(15),
            reader.GetFieldValue<DateTimeOffset>(16),
            state,
            reader.IsDBNull(18)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(18));
        ValidateTransfer(transfer);
        return transfer;
    }

    private static void ValidateTransfer(PrivateSnapshotTransferRecord transfer)
    {
        ValidateTransferId(transfer.Id);
        ValidateOwnerAndInstallation(
            transfer.OwnerProvider,
            transfer.OwnerExternalId,
            transfer.SourceInstallationId);
        ValidateText(transfer.ProviderUploadId, "Provider upload ID", 512);

        new OwnedWorldSnapshot(
            transfer.WorldId,
            transfer.OwnerProvider,
            transfer.OwnerExternalId,
            transfer.SourceInstallationId,
            transfer.StateRevisionId,
            transfer.EnvironmentRevisionId,
            transfer.GameAdapterId,
            transfer.ObjectKey,
            transfer.ExpectedByteSize,
            transfer.ExpectedSha256,
            transfer.EnvironmentManifest,
            transfer.CreatedAt).Validate();

        if (transfer.PartSizeBytes <= 0 || transfer.PartCount <= 0)
        {
            throw new InvalidDataException(
                "Persisted private transfer part size and count must be positive.");
        }

        var expectedPartCount = checked((int)(
            (transfer.ExpectedByteSize + transfer.PartSizeBytes - 1L) /
            transfer.PartSizeBytes));
        if (transfer.PartCount != expectedPartCount)
        {
            throw new InvalidDataException(
                "Persisted private transfer part count does not match its exact package size and part size.");
        }

        if (transfer.ExpiresAt <= transfer.CreatedAt)
        {
            throw new InvalidDataException(
                "Persisted private transfer expiry must be later than creation time.");
        }

        var terminal = transfer.State is not PrivateSnapshotTransferState.Provisioning and
            not PrivateSnapshotTransferState.Active;
        if (terminal != (transfer.StateChangedAt is not null))
        {
            throw new InvalidDataException(
                "Persisted private transfer state-change time disagrees with its state.");
        }
    }

    private static void AddTransferParameters(
        NpgsqlCommand command,
        PrivateSnapshotTransferRecord transfer)
    {
        command.Parameters.AddWithValue("transfer_id", NpgsqlDbType.Uuid, transfer.Id.Value);
        command.Parameters.AddWithValue("owner_provider", NpgsqlDbType.Text, transfer.OwnerProvider);
        command.Parameters.AddWithValue("owner_external_id", NpgsqlDbType.Text, transfer.OwnerExternalId);
        command.Parameters.AddWithValue("source_installation_id", NpgsqlDbType.Text, transfer.SourceInstallationId);
        command.Parameters.AddWithValue("world_id", NpgsqlDbType.Uuid, transfer.WorldId.Value);
        command.Parameters.AddWithValue("state_revision_id", NpgsqlDbType.Uuid, transfer.StateRevisionId.Value);
        command.Parameters.AddWithValue("environment_revision_id", NpgsqlDbType.Uuid, transfer.EnvironmentRevisionId.Value);
        command.Parameters.AddWithValue("game_adapter_id", NpgsqlDbType.Text, transfer.GameAdapterId);
        command.Parameters.AddWithValue("object_key", NpgsqlDbType.Text, transfer.ObjectKey);
        command.Parameters.AddWithValue("provider_upload_id", NpgsqlDbType.Text, transfer.ProviderUploadId);
        command.Parameters.AddWithValue("expected_byte_size", NpgsqlDbType.Bigint, transfer.ExpectedByteSize);
        command.Parameters.AddWithValue("expected_sha256", NpgsqlDbType.Text, transfer.ExpectedSha256);
        command.Parameters.AddWithValue(
            "environment_manifest_json",
            NpgsqlDbType.Text,
            JsonSerializer.Serialize(transfer.EnvironmentManifest, ManifestJsonOptions));
        command.Parameters.AddWithValue("part_size_bytes", NpgsqlDbType.Integer, transfer.PartSizeBytes);
        command.Parameters.AddWithValue("part_count", NpgsqlDbType.Integer, transfer.PartCount);
        command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, transfer.CreatedAt);
        command.Parameters.AddWithValue("expires_at", NpgsqlDbType.TimestampTz, transfer.ExpiresAt);
        command.Parameters.AddWithValue("state", NpgsqlDbType.Text, transfer.State.ToString());
    }

    private static void AddOwnerTransferParameters(
        NpgsqlCommand command,
        PrivateSnapshotTransferId transferId,
        string ownerProvider,
        string ownerExternalId,
        string sourceInstallationId)
    {
        command.Parameters.AddWithValue("transfer_id", NpgsqlDbType.Uuid, transferId.Value);
        command.Parameters.AddWithValue("owner_provider", NpgsqlDbType.Text, ownerProvider);
        command.Parameters.AddWithValue("owner_external_id", NpgsqlDbType.Text, ownerExternalId);
        command.Parameters.AddWithValue("source_installation_id", NpgsqlDbType.Text, sourceInstallationId);
    }

    private static void ValidateTransferId(PrivateSnapshotTransferId transferId)
    {
        if (transferId.Value == Guid.Empty)
        {
            throw new ArgumentException("Private snapshot transfer ID is required.", nameof(transferId));
        }
    }

    private static void ValidateOwnerAndInstallation(
        string ownerProvider,
        string ownerExternalId,
        string sourceInstallationId)
    {
        ValidateText(ownerProvider, "Owner provider", 128);
        ValidateText(ownerExternalId, "Owner external ID", 256);
        ValidateText(sourceInstallationId, "Source installation ID", 128);
    }

    private static void ValidateText(string value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"{name} must be non-empty, contain no control characters, and be at most {maximumLength} characters.");
        }
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
        => new(value.Ticks - (value.Ticks % 10), value.Offset);
}
