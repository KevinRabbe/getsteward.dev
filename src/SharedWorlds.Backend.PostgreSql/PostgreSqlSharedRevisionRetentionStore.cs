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
        if (retainedCanonicalHeadCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedCanonicalHeadCount));
        }

        if (limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

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
            var objectKeyOrdinal = reader.GetOrdinal("hosted_object_key");
            results.Add(new SharedRevisionCleanupCandidate(
                new WorldId(reader.GetGuid(reader.GetOrdinal("world_id"))),
                new RevisionId(reader.GetGuid(reader.GetOrdinal("revision_id"))),
                (SharedPackageKind)reader.GetInt16(reader.GetOrdinal("kind")),
                reader.IsDBNull(objectKeyOrdinal) ? null : reader.GetString(objectKeyOrdinal),
                reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("published_at")),
                reader.GetBoolean(reader.GetOrdinal("was_canonical"))));
        }

        return results;
    }

    private static void ValidateWorldId(WorldId worldId)
    {
        if (worldId.Value == Guid.Empty)
        {
            throw new ArgumentException("World ID is required.", nameof(worldId));
        }
    }
}
