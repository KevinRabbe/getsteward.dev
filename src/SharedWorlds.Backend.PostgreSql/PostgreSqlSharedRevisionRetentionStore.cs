using Npgsql;
using SharedWorlds.Backend.Transfers;
using SharedWorlds.Backend.Worlds;
using SharedWorlds.Core.Domain;

namespace SharedWorlds.Backend.PostgreSql;

public sealed class PostgreSqlSharedRevisionRetentionStore : ISharedRevisionRetentionStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgreSqlSharedRevisionRetentionStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<SharedCanonicalHeadRecord>> ListRecentCanonicalHeadsAsync(
        WorldId worldId,
        int count,
        CancellationToken cancellationToken = default)
    {
        ValidateWorldId(worldId);
        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        const string sql = """
            SELECT
                world_id,
                sequence,
                state_revision_id,
                environment_revision_id,
                committed_at
            FROM steward_world_canonical_heads
            WHERE world_id = @world_id
            ORDER BY sequence DESC
            LIMIT @count;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("count", count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<SharedCanonicalHeadRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var environmentOrdinal = reader.GetOrdinal("environment_revision_id");
            results.Add(new SharedCanonicalHeadRecord(
                new WorldId(reader.GetGuid(reader.GetOrdinal("world_id"))),
                reader.GetInt64(reader.GetOrdinal("sequence")),
                new SharedWorldHead(
                    new RevisionId(reader.GetGuid(reader.GetOrdinal("state_revision_id"))),
                    reader.IsDBNull(environmentOrdinal)
                        ? null
                        : new RevisionId(reader.GetGuid(environmentOrdinal))),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("committed_at"))));
        }

        return results;
    }

    public async Task<IReadOnlyList<SharedRevisionCleanupCandidate>> ListCleanupEligibleAsync(
        DateTimeOffset uncommittedCandidateCutoff,
        int retainedCanonicalHeadCount,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ValidateRetentionArguments(retainedCanonicalHeadCount, limit);

        const string sql = """
            WITH ranked_heads AS (
                SELECT
                    world_id,
                    state_revision_id,
                    environment_revision_id,
                    ROW_NUMBER() OVER (
                        PARTITION BY world_id
                        ORDER BY sequence DESC) AS retention_rank
                FROM steward_world_canonical_heads
            ),
            retained_heads AS (
                SELECT world_id, state_revision_id, environment_revision_id
                FROM ranked_heads
                WHERE retention_rank <= @retained_head_count
            ),
            cleanup_candidates AS (
                SELECT
                    s.world_id,
                    s.revision_id,
                    0::smallint AS kind,
                    s.package_object_key AS hosted_object_key,
                    s.published_at,
                    EXISTS (
                        SELECT 1
                        FROM steward_world_canonical_heads h
                        WHERE h.world_id = s.world_id
                          AND h.state_revision_id = s.revision_id) AS was_canonical
                FROM steward_state_revisions s
                WHERE NOT EXISTS (
                        SELECT 1
                        FROM retained_heads h
                        WHERE h.world_id = s.world_id
                          AND h.state_revision_id = s.revision_id)
                  AND NOT EXISTS (
                        SELECT 1
                        FROM steward_shared_worlds w
                        WHERE w.world_id = s.world_id
                          AND w.current_state_revision_id = s.revision_id)
                  AND NOT EXISTS (
                        SELECT 1
                        FROM steward_world_reservations r
                        WHERE r.world_id = s.world_id
                          AND r.starting_state_revision_id = s.revision_id)
                  AND NOT EXISTS (
                        SELECT 1
                        FROM steward_package_transfers t
                        WHERE t.world_id = s.world_id
                          AND t.revision_id = s.revision_id
                          AND t.kind = 0
                          AND t.state IN (0, 2, 3))
                  AND NOT EXISTS (
                        SELECT 1
                        FROM steward_package_transfers t
                        INNER JOIN steward_world_reservations r
                            ON r.world_id = t.world_id
                           AND r.holder_provider = t.owner_provider
                           AND r.holder_external_id = t.owner_external_id
                        WHERE t.world_id = s.world_id
                          AND t.revision_id = s.revision_id
                          AND t.kind = 0)
                  AND (
                        EXISTS (
                            SELECT 1
                            FROM steward_world_canonical_heads h
                            WHERE h.world_id = s.world_id
                              AND h.state_revision_id = s.revision_id)
                        OR s.published_at <= @uncommitted_cutoff)

                UNION ALL

                SELECT
                    e.world_id,
                    e.revision_id,
                    1::smallint AS kind,
                    CASE WHEN e.byte_size IS NULL THEN NULL ELSE e.artifact_reference END AS hosted_object_key,
                    e.published_at,
                    EXISTS (
                        SELECT 1
                        FROM steward_world_canonical_heads h
                        WHERE h.world_id = e.world_id
                          AND h.environment_revision_id = e.revision_id) AS was_canonical
                FROM steward_environment_revisions e
                WHERE NOT EXISTS (
                        SELECT 1
                        FROM retained_heads h
                        WHERE h.world_id = e.world_id
                          AND h.environment_revision_id = e.revision_id)
                  AND NOT EXISTS (
                        SELECT 1
                        FROM steward_shared_worlds w
                        WHERE w.world_id = e.world_id
                          AND w.current_environment_revision_id = e.revision_id)
                  AND NOT EXISTS (
                        SELECT 1
                        FROM steward_world_reservations r
                        WHERE r.world_id = e.world_id
                          AND r.starting_environment_revision_id = e.revision_id)
                  AND NOT EXISTS (
                        SELECT 1
                        FROM steward_state_revisions s
                        WHERE s.world_id = e.world_id
                          AND s.required_environment_revision_id = e.revision_id)
                  AND NOT EXISTS (
                        SELECT 1
                        FROM steward_package_transfers t
                        WHERE t.world_id = e.world_id
                          AND t.required_environment_revision_id = e.revision_id)
                  AND NOT EXISTS (
                        SELECT 1
                        FROM steward_package_transfers t
                        WHERE t.world_id = e.world_id
                          AND t.revision_id = e.revision_id
                          AND t.kind = 1
                          AND t.state IN (0, 2, 3))
                  AND NOT EXISTS (
                        SELECT 1
                        FROM steward_package_transfers t
                        INNER JOIN steward_world_reservations r
                            ON r.world_id = t.world_id
                           AND r.holder_provider = t.owner_provider
                           AND r.holder_external_id = t.owner_external_id
                        WHERE t.world_id = e.world_id
                          AND t.revision_id = e.revision_id
                          AND t.kind = 1)
                  AND (
                        EXISTS (
                            SELECT 1
                            FROM steward_world_canonical_heads h
                            WHERE h.world_id = e.world_id
                              AND h.environment_revision_id = e.revision_id)
                        OR e.published_at <= @uncommitted_cutoff)
            )
            SELECT
                world_id,
                revision_id,
                kind,
                hosted_object_key,
                published_at,
                was_canonical
            FROM cleanup_candidates
            ORDER BY published_at, world_id, revision_id
            LIMIT @limit;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("retained_head_count", retainedCanonicalHeadCount);
        command.Parameters.AddWithValue("uncommitted_cutoff", uncommittedCandidateCutoff);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<SharedRevisionCleanupCandidate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadCleanupCandidate(reader));
        }

        return results;
    }

    public async Task<RetireSharedRevisionStatus> TryRetireCleanupCandidateAsync(
        SharedRevisionCleanupCandidate candidate,
        DateTimeOffset uncommittedCandidateCutoff,
        int retainedCanonicalHeadCount,
        DateTimeOffset retiredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ValidateWorldId(candidate.WorldId);
        if (candidate.RevisionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Revision ID is required.", nameof(candidate));
        }

        if (retainedCanonicalHeadCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedCanonicalHeadCount));
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (!await LockWorldAsync(connection, transaction, candidate.WorldId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return RetireSharedRevisionStatus.NotFound;
        }

        var retired = candidate.Kind switch
        {
            SharedPackageKind.State => await TryRetireStateAsync(
                connection,
                transaction,
                candidate.WorldId,
                candidate.RevisionId,
                uncommittedCandidateCutoff,
                retainedCanonicalHeadCount,
                retiredAt,
                cancellationToken),
            SharedPackageKind.Environment => await TryRetireEnvironmentAsync(
                connection,
                transaction,
                candidate.WorldId,
                candidate.RevisionId,
                uncommittedCandidateCutoff,
                retainedCanonicalHeadCount,
                retiredAt,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(candidate))
        };

        if (retired)
        {
            await transaction.CommitAsync(cancellationToken);
            return RetireSharedRevisionStatus.Retired;
        }

        var exists = await RevisionExistsAsync(
            connection,
            transaction,
            candidate.WorldId,
            candidate.RevisionId,
            candidate.Kind,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return exists
            ? RetireSharedRevisionStatus.NoLongerEligible
            : RetireSharedRevisionStatus.NotFound;
    }

    public async Task<IReadOnlyList<SharedImmutableObjectCleanupRecord>> ListPendingObjectCleanupAsync(
        DateTimeOffset retryAtOrBefore,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        const string sql = """
            SELECT object_key, enqueued_at, attempt_count, last_attempt_at
            FROM steward_object_cleanup_queue
            WHERE last_attempt_at IS NULL
               OR last_attempt_at <= @retry_at_or_before
            ORDER BY COALESCE(last_attempt_at, enqueued_at), enqueued_at, object_key
            LIMIT @limit;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("retry_at_or_before", retryAtOrBefore);
        command.Parameters.AddWithValue("limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<SharedImmutableObjectCleanupRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var lastAttemptOrdinal = reader.GetOrdinal("last_attempt_at");
            results.Add(new SharedImmutableObjectCleanupRecord(
                reader.GetString(reader.GetOrdinal("object_key")),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("enqueued_at")),
                reader.GetInt32(reader.GetOrdinal("attempt_count")),
                reader.IsDBNull(lastAttemptOrdinal)
                    ? null
                    : reader.GetFieldValue<DateTimeOffset>(lastAttemptOrdinal)));
        }

        return results;
    }

    public async Task<bool> TryRecordObjectCleanupFailureAsync(
        string objectKey,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);
        const string sql = """
            UPDATE steward_object_cleanup_queue
            SET attempt_count = attempt_count + 1,
                last_attempt_at = @attempted_at
            WHERE object_key = @object_key;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("attempted_at", attemptedAt);
        command.Parameters.AddWithValue("object_key", objectKey);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> TryCompleteObjectCleanupAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(objectKey);
        const string sql = """
            DELETE FROM steward_object_cleanup_queue
            WHERE object_key = @object_key;
            """;

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("object_key", objectKey);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<bool> TryRetireStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        RevisionId revisionId,
        DateTimeOffset uncommittedCandidateCutoff,
        int retainedCanonicalHeadCount,
        DateTimeOffset retiredAt,
        CancellationToken cancellationToken)
    {
        const string selectSql = """
            WITH ranked_heads AS (
                SELECT
                    state_revision_id,
                    ROW_NUMBER() OVER (ORDER BY sequence DESC) AS retention_rank
                FROM steward_world_canonical_heads
                WHERE world_id = @world_id
            )
            SELECT s.package_object_key
            FROM steward_state_revisions s
            WHERE s.world_id = @world_id
              AND s.revision_id = @revision_id
              AND NOT EXISTS (
                    SELECT 1
                    FROM ranked_heads h
                    WHERE h.retention_rank <= @retained_head_count
                      AND h.state_revision_id = s.revision_id)
              AND NOT EXISTS (
                    SELECT 1
                    FROM steward_shared_worlds w
                    WHERE w.world_id = s.world_id
                      AND w.current_state_revision_id = s.revision_id)
              AND NOT EXISTS (
                    SELECT 1
                    FROM steward_world_reservations r
                    WHERE r.world_id = s.world_id
                      AND r.starting_state_revision_id = s.revision_id)
              AND NOT EXISTS (
                    SELECT 1
                    FROM steward_package_transfers t
                    WHERE t.world_id = s.world_id
                      AND t.revision_id = s.revision_id
                      AND t.kind = 0
                      AND t.state IN (0, 2, 3))
              AND NOT EXISTS (
                    SELECT 1
                    FROM steward_package_transfers t
                    INNER JOIN steward_world_reservations r
                        ON r.world_id = t.world_id
                       AND r.holder_provider = t.owner_provider
                       AND r.holder_external_id = t.owner_external_id
                    WHERE t.world_id = s.world_id
                      AND t.revision_id = s.revision_id
                      AND t.kind = 0)
              AND (
                    EXISTS (
                        SELECT 1
                        FROM steward_world_canonical_heads h
                        WHERE h.world_id = s.world_id
                          AND h.state_revision_id = s.revision_id)
                    OR s.published_at <= @uncommitted_cutoff)
            FOR UPDATE OF s;
            """;

        await using var select = new NpgsqlCommand(selectSql, connection, transaction);
        select.Parameters.AddWithValue("world_id", worldId.Value);
        select.Parameters.AddWithValue("revision_id", revisionId.Value);
        select.Parameters.AddWithValue("retained_head_count", retainedCanonicalHeadCount);
        select.Parameters.AddWithValue("uncommitted_cutoff", uncommittedCandidateCutoff);
        var objectKey = await select.ExecuteScalarAsync(cancellationToken) as string;
        if (objectKey is null)
        {
            return false;
        }

        await EnqueueObjectCleanupAsync(connection, transaction, objectKey, retiredAt, cancellationToken);
        await DeleteResolvedTransfersAsync(
            connection,
            transaction,
            worldId,
            revisionId,
            SharedPackageKind.State,
            cancellationToken);

        const string deleteSql = """
            DELETE FROM steward_state_revisions
            WHERE world_id = @world_id
              AND revision_id = @revision_id;
            """;
        await using var delete = new NpgsqlCommand(deleteSql, connection, transaction);
        delete.Parameters.AddWithValue("world_id", worldId.Value);
        delete.Parameters.AddWithValue("revision_id", revisionId.Value);
        return await delete.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<bool> TryRetireEnvironmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        RevisionId revisionId,
        DateTimeOffset uncommittedCandidateCutoff,
        int retainedCanonicalHeadCount,
        DateTimeOffset retiredAt,
        CancellationToken cancellationToken)
    {
        const string selectSql = """
            WITH ranked_heads AS (
                SELECT
                    environment_revision_id,
                    ROW_NUMBER() OVER (ORDER BY sequence DESC) AS retention_rank
                FROM steward_world_canonical_heads
                WHERE world_id = @world_id
            )
            SELECT e.artifact_reference, e.byte_size
            FROM steward_environment_revisions e
            WHERE e.world_id = @world_id
              AND e.revision_id = @revision_id
              AND NOT EXISTS (
                    SELECT 1
                    FROM ranked_heads h
                    WHERE h.retention_rank <= @retained_head_count
                      AND h.environment_revision_id = e.revision_id)
              AND NOT EXISTS (
                    SELECT 1
                    FROM steward_shared_worlds w
                    WHERE w.world_id = e.world_id
                      AND w.current_environment_revision_id = e.revision_id)
              AND NOT EXISTS (
                    SELECT 1
                    FROM steward_world_reservations r
                    WHERE r.world_id = e.world_id
                      AND r.starting_environment_revision_id = e.revision_id)
              AND NOT EXISTS (
                    SELECT 1
                    FROM steward_state_revisions s
                    WHERE s.world_id = e.world_id
                      AND s.required_environment_revision_id = e.revision_id)
              AND NOT EXISTS (
                    SELECT 1
                    FROM steward_package_transfers t
                    WHERE t.world_id = e.world_id
                      AND t.required_environment_revision_id = e.revision_id)
              AND NOT EXISTS (
                    SELECT 1
                    FROM steward_package_transfers t
                    WHERE t.world_id = e.world_id
                      AND t.revision_id = e.revision_id
                      AND t.kind = 1
                      AND t.state IN (0, 2, 3))
              AND NOT EXISTS (
                    SELECT 1
                    FROM steward_package_transfers t
                    INNER JOIN steward_world_reservations r
                        ON r.world_id = t.world_id
                       AND r.holder_provider = t.owner_provider
                       AND r.holder_external_id = t.owner_external_id
                    WHERE t.world_id = e.world_id
                      AND t.revision_id = e.revision_id
                      AND t.kind = 1)
              AND (
                    EXISTS (
                        SELECT 1
                        FROM steward_world_canonical_heads h
                        WHERE h.world_id = e.world_id
                          AND h.environment_revision_id = e.revision_id)
                    OR e.published_at <= @uncommitted_cutoff)
            FOR UPDATE OF e;
            """;

        await using var select = new NpgsqlCommand(selectSql, connection, transaction);
        select.Parameters.AddWithValue("world_id", worldId.Value);
        select.Parameters.AddWithValue("revision_id", revisionId.Value);
        select.Parameters.AddWithValue("retained_head_count", retainedCanonicalHeadCount);
        select.Parameters.AddWithValue("uncommitted_cutoff", uncommittedCandidateCutoff);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return false;
        }

        var artifactReference = reader.GetString(reader.GetOrdinal("artifact_reference"));
        var byteSizeOrdinal = reader.GetOrdinal("byte_size");
        var hosted = !reader.IsDBNull(byteSizeOrdinal);
        await reader.DisposeAsync();

        if (hosted)
        {
            await EnqueueObjectCleanupAsync(
                connection,
                transaction,
                artifactReference,
                retiredAt,
                cancellationToken);
        }

        await DeleteResolvedTransfersAsync(
            connection,
            transaction,
            worldId,
            revisionId,
            SharedPackageKind.Environment,
            cancellationToken);

        const string deleteSql = """
            DELETE FROM steward_environment_revisions
            WHERE world_id = @world_id
              AND revision_id = @revision_id;
            """;
        await using var delete = new NpgsqlCommand(deleteSql, connection, transaction);
        delete.Parameters.AddWithValue("world_id", worldId.Value);
        delete.Parameters.AddWithValue("revision_id", revisionId.Value);
        return await delete.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<bool> LockWorldAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT 1
            FROM steward_shared_worlds
            WHERE world_id = @world_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task DeleteResolvedTransfersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        RevisionId revisionId,
        SharedPackageKind kind,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM steward_package_transfers
            WHERE world_id = @world_id
              AND revision_id = @revision_id
              AND kind = @kind
              AND state IN (1, 4);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("revision_id", revisionId.Value);
        command.Parameters.AddWithValue("kind", (short)kind);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnqueueObjectCleanupAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string objectKey,
        DateTimeOffset enqueuedAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO steward_object_cleanup_queue (
                object_key,
                enqueued_at,
                attempt_count,
                last_attempt_at)
            VALUES (
                @object_key,
                @enqueued_at,
                0,
                NULL)
            ON CONFLICT (object_key) DO NOTHING;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("object_key", objectKey);
        command.Parameters.AddWithValue("enqueued_at", enqueuedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> RevisionExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldId worldId,
        RevisionId revisionId,
        SharedPackageKind kind,
        CancellationToken cancellationToken)
    {
        var table = kind switch
        {
            SharedPackageKind.State => "steward_state_revisions",
            SharedPackageKind.Environment => "steward_environment_revisions",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var sql = $"SELECT 1 FROM {table} WHERE world_id = @world_id AND revision_id = @revision_id;";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("world_id", worldId.Value);
        command.Parameters.AddWithValue("revision_id", revisionId.Value);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static SharedRevisionCleanupCandidate ReadCleanupCandidate(NpgsqlDataReader reader)
    {
        var objectKeyOrdinal = reader.GetOrdinal("hosted_object_key");
        return new SharedRevisionCleanupCandidate(
            new WorldId(reader.GetGuid(reader.GetOrdinal("world_id"))),
            new RevisionId(reader.GetGuid(reader.GetOrdinal("revision_id"))),
            (SharedPackageKind)reader.GetInt16(reader.GetOrdinal("kind")),
            reader.IsDBNull(objectKeyOrdinal) ? null : reader.GetString(objectKeyOrdinal),
            reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("published_at")),
            reader.GetBoolean(reader.GetOrdinal("was_canonical")));
    }

    private static void ValidateRetentionArguments(int retainedCanonicalHeadCount, int limit)
    {
        if (retainedCanonicalHeadCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedCanonicalHeadCount));
        }

        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
    }

    private static void ValidateWorldId(WorldId worldId)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }
    }

    private static void ValidateObjectKey(string objectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        if (objectKey.Length > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(objectKey));
        }
    }
}
