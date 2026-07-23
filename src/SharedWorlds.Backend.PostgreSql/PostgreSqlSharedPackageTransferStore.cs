using Npgsql;
using NpgsqlTypes;
using SharedWorlds.Backend.Identity;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.PostgreSql;

public sealed class PostgreSqlSharedPackageTransferStore : ISharedPackageTransferStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlSharedPackageTransferStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<bool> TryCreateAsync(
        SharedPackageTransferRecord transfer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        const string sql = """
            INSERT INTO steward_package_transfers (
                transfer_id,
                world_id,
                revision_id,
                kind,
                adapter_id,
                owner_provider,
                owner_external_id,
                object_key,
                provider_upload_id,
                expected_byte_size,
                expected_sha256,
                required_environment_revision_id,
                part_size_bytes,
                part_count,
                created_at,
                expires_at,
                state,
                finalized_at)
            VALUES (
                @transfer_id,
                @world_id,
                @revision_id,
                @kind,
                @adapter_id,
                @owner_provider,
                @owner_external_id,
                @object_key,
                @provider_upload_id,
                @expected_byte_size,
                @expected_sha256,
                @required_environment_revision_id,
                @part_size_bytes,
                @part_count,
                @created_at,
                @expires_at,
                @state,
                @finalized_at)
            ON CONFLICT DO NOTHING;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        AddTransferParameters(command, transfer);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<SharedPackageTransferRecord?> LoadAsync(
        SharedPackageTransferId transferId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                transfer_id,
                world_id,
                revision_id,
                kind,
                adapter_id,
                owner_provider,
                owner_external_id,
                object_key,
                provider_upload_id,
                expected_byte_size,
                expected_sha256,
                required_environment_revision_id,
                part_size_bytes,
                part_count,
                created_at,
                expires_at,
                state,
                finalized_at
            FROM steward_package_transfers
            WHERE transfer_id = @transfer_id;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("transfer_id", transferId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTransfer(reader) : null;
    }

    public async Task<SharedPackageTransferRecord?> LoadInFlightByObjectKeyAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        const string sql = """
            SELECT
                transfer_id,
                world_id,
                revision_id,
                kind,
                adapter_id,
                owner_provider,
                owner_external_id,
                object_key,
                provider_upload_id,
                expected_byte_size,
                expected_sha256,
                required_environment_revision_id,
                part_size_bytes,
                part_count,
                created_at,
                expires_at,
                state,
                finalized_at
            FROM steward_package_transfers
            WHERE object_key = @object_key
              AND state IN (@active_state, @provisioning_state);
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("object_key", objectKey);
        command.Parameters.AddWithValue("active_state", (short)SharedPackageTransferState.Active);
        command.Parameters.AddWithValue("provisioning_state", (short)SharedPackageTransferState.Provisioning);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTransfer(reader) : null;
    }

    public async Task<bool> TryActivateProvisioningAsync(
        SharedPackageTransferId transferId,
        ExternalIdentityRef expectedOwner,
        string expectedPlaceholderProviderUploadId,
        string providerUploadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPlaceholderProviderUploadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerUploadId);

        const string sql = """
            UPDATE steward_package_transfers
            SET provider_upload_id = @provider_upload_id,
                state = @active_state
            WHERE transfer_id = @transfer_id
              AND owner_provider = @owner_provider
              AND owner_external_id = @owner_external_id
              AND provider_upload_id = @expected_provider_upload_id
              AND state = @provisioning_state;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("provider_upload_id", providerUploadId);
        command.Parameters.AddWithValue("active_state", (short)SharedPackageTransferState.Active);
        command.Parameters.AddWithValue("transfer_id", transferId.Value);
        command.Parameters.AddWithValue("owner_provider", expectedOwner.Provider);
        command.Parameters.AddWithValue("owner_external_id", expectedOwner.ExternalId);
        command.Parameters.AddWithValue("expected_provider_upload_id", expectedPlaceholderProviderUploadId);
        command.Parameters.AddWithValue("provisioning_state", (short)SharedPackageTransferState.Provisioning);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> TrySetStateAsync(
        SharedPackageTransferId transferId,
        ExternalIdentityRef expectedOwner,
        SharedPackageTransferState expectedState,
        SharedPackageTransferState nextState,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        const string sql = """
            UPDATE steward_package_transfers
            SET state = @next_state,
                finalized_at = CASE
                    WHEN @next_state = @finalized_state THEN @changed_at
                    ELSE finalized_at
                END
            WHERE transfer_id = @transfer_id
              AND owner_provider = @owner_provider
              AND owner_external_id = @owner_external_id
              AND state = @expected_state;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("next_state", (short)nextState);
        command.Parameters.AddWithValue("finalized_state", (short)SharedPackageTransferState.Finalized);
        command.Parameters.AddWithValue("changed_at", changedAt);
        command.Parameters.AddWithValue("transfer_id", transferId.Value);
        command.Parameters.AddWithValue("owner_provider", expectedOwner.Provider);
        command.Parameters.AddWithValue("owner_external_id", expectedOwner.ExternalId);
        command.Parameters.AddWithValue("expected_state", (short)expectedState);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<SharedPackageTransferRecord>> ListByStateExpiringBeforeAsync(
        SharedPackageTransferState state,
        DateTimeOffset expiresAtOrBefore,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        const string sql = """
            SELECT
                transfer_id,
                world_id,
                revision_id,
                kind,
                adapter_id,
                owner_provider,
                owner_external_id,
                object_key,
                provider_upload_id,
                expected_byte_size,
                expected_sha256,
                required_environment_revision_id,
                part_size_bytes,
                part_count,
                created_at,
                expires_at,
                state,
                finalized_at
            FROM steward_package_transfers
            WHERE state = @state
              AND expires_at <= @expires_at_or_before
            ORDER BY expires_at, transfer_id
            LIMIT @limit;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("state", (short)state);
        command.Parameters.AddWithValue("expires_at_or_before", expiresAtOrBefore);
        command.Parameters.AddWithValue("limit", limit);

        var transfers = new List<SharedPackageTransferRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            transfers.Add(ReadTransfer(reader));
        }

        return transfers;
    }

    public async Task<bool> TryDeleteAsync(
        SharedPackageTransferId transferId,
        ExternalIdentityRef expectedOwner,
        SharedPackageTransferState expectedState,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedOwner);
        const string sql = """
            DELETE FROM steward_package_transfers
            WHERE transfer_id = @transfer_id
              AND owner_provider = @owner_provider
              AND owner_external_id = @owner_external_id
              AND state = @expected_state;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("transfer_id", transferId.Value);
        command.Parameters.AddWithValue("owner_provider", expectedOwner.Provider);
        command.Parameters.AddWithValue("owner_external_id", expectedOwner.ExternalId);
        command.Parameters.AddWithValue("expected_state", (short)expectedState);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static void AddTransferParameters(NpgsqlCommand command, SharedPackageTransferRecord transfer)
    {
        command.Parameters.AddWithValue("transfer_id", transfer.Id.Value);
        command.Parameters.AddWithValue("world_id", transfer.WorldId.Value);
        command.Parameters.AddWithValue("revision_id", transfer.RevisionId.Value);
        command.Parameters.AddWithValue("kind", (short)transfer.Kind);
        command.Parameters.AddWithValue("adapter_id", transfer.AdapterId);
        command.Parameters.AddWithValue("owner_provider", transfer.Owner.Provider);
        command.Parameters.AddWithValue("owner_external_id", transfer.Owner.ExternalId);
        command.Parameters.AddWithValue("object_key", transfer.ObjectKey);
        command.Parameters.AddWithValue("provider_upload_id", transfer.ProviderUploadId);
        command.Parameters.AddWithValue("expected_byte_size", transfer.ExpectedByteSize);
        command.Parameters.AddWithValue("expected_sha256", transfer.ExpectedSha256.ToUpperInvariant());
        command.Parameters.Add(new NpgsqlParameter("required_environment_revision_id", NpgsqlDbType.Uuid)
        {
            Value = transfer.RequiredEnvironmentRevisionId is { } environmentRevision
                ? environmentRevision.Value
                : DBNull.Value
        });
        command.Parameters.AddWithValue("part_size_bytes", transfer.PartSizeBytes);
        command.Parameters.AddWithValue("part_count", transfer.PartCount);
        command.Parameters.AddWithValue("created_at", transfer.CreatedAt);
        command.Parameters.AddWithValue("expires_at", transfer.ExpiresAt);
        command.Parameters.AddWithValue("state", (short)transfer.State);
        command.Parameters.Add(new NpgsqlParameter("finalized_at", NpgsqlDbType.TimestampTz)
        {
            Value = transfer.FinalizedAt is { } finalizedAt ? finalizedAt : DBNull.Value
        });
    }

    private static SharedPackageTransferRecord ReadTransfer(NpgsqlDataReader reader)
    {
        var environmentOrdinal = reader.GetOrdinal("required_environment_revision_id");
        var finalizedOrdinal = reader.GetOrdinal("finalized_at");
        return new SharedPackageTransferRecord(
            new SharedPackageTransferId(reader.GetGuid(reader.GetOrdinal("transfer_id"))),
            new WorldId(reader.GetGuid(reader.GetOrdinal("world_id"))),
            new RevisionId(reader.GetGuid(reader.GetOrdinal("revision_id"))),
            (SharedPackageKind)reader.GetInt16(reader.GetOrdinal("kind")),
            reader.GetString(reader.GetOrdinal("adapter_id")),
            new ExternalIdentityRef(
                reader.GetString(reader.GetOrdinal("owner_provider")),
                reader.GetString(reader.GetOrdinal("owner_external_id"))),
            reader.GetString(reader.GetOrdinal("object_key")),
            reader.GetString(reader.GetOrdinal("provider_upload_id")),
            reader.GetInt64(reader.GetOrdinal("expected_byte_size")),
            reader.GetString(reader.GetOrdinal("expected_sha256")),
            reader.IsDBNull(environmentOrdinal)
                ? null
                : new RevisionId(reader.GetGuid(environmentOrdinal)),
            reader.GetInt32(reader.GetOrdinal("part_size_bytes")),
            reader.GetInt32(reader.GetOrdinal("part_count")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("expires_at")),
            (SharedPackageTransferState)reader.GetInt16(reader.GetOrdinal("state")),
            reader.IsDBNull(finalizedOrdinal)
                ? null
                : reader.GetFieldValue<DateTimeOffset>(finalizedOrdinal));
    }
}
