using Npgsql;
using NpgsqlTypes;
using SharedWorlds.Core.Domain;
using SharedWorlds.Core.Worlds;

namespace SharedWorlds.Backend.PostgreSql;

/// <summary>
/// Production authority decorator for Bring Here snapshot reads. The core resolver asks for
/// MaximumSnapshotsPerWorld + 1 so overflow can be detected without silently truncating authority
/// evidence. The underlying persistence store retains its normal public bound; this decorator performs
/// the bounded count probe and delegates exact persistence operations unchanged.
/// </summary>
public sealed class PostgreSqlBringHereOwnedWorldSnapshotStore : IOwnedWorldSnapshotStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlOwnedWorldSnapshotStore _inner;

    public PostgreSqlBringHereOwnedWorldSnapshotStore(
        NpgsqlDataSource dataSource,
        PostgreSqlOwnedWorldSnapshotStore inner)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(inner);
        _dataSource = dataSource;
        _inner = inner;
    }

    public Task<OwnedWorldSnapshot?> LoadExactAsync(
        string ownerProvider,
        string ownerExternalId,
        WorldId worldId,
        string installationId,
        RevisionId stateRevisionId,
        RevisionId environmentRevisionId,
        CancellationToken cancellationToken = default)
        => _inner.LoadExactAsync(
            ownerProvider,
            ownerExternalId,
            worldId,
            installationId,
            stateRevisionId,
            environmentRevisionId,
            cancellationToken);

    public async Task<IReadOnlyList<OwnedWorldSnapshot>> ListWorldSnapshotsAsync(
        string ownerProvider,
        string ownerExternalId,
        WorldId worldId,
        int maximumSnapshots,
        CancellationToken cancellationToken = default)
    {
        var normalLimit = BringHereSnapshotAuthorityService.MaximumSnapshotsPerWorld;
        if (maximumSnapshots is < 1 || maximumSnapshots > normalLimit + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSnapshots));
        }

        if (maximumSnapshots <= normalLimit)
        {
            return await _inner.ListWorldSnapshotsAsync(
                ownerProvider,
                ownerExternalId,
                worldId,
                maximumSnapshots,
                cancellationToken);
        }

        const string sql = """
            SELECT count(*)
              FROM (
                    SELECT 1
                      FROM steward_owned_world_snapshots
                     WHERE world_id = @world_id
                       AND owner_provider = @owner_provider
                       AND owner_external_id = @owner_external_id
                     LIMIT @overflow_probe_limit
                   ) AS bounded_snapshot_probe;
            """;
        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("world_id", NpgsqlDbType.Uuid, worldId.Value);
        command.Parameters.AddWithValue("owner_provider", NpgsqlDbType.Text, ownerProvider);
        command.Parameters.AddWithValue("owner_external_id", NpgsqlDbType.Text, ownerExternalId);
        command.Parameters.AddWithValue(
            "overflow_probe_limit",
            NpgsqlDbType.Integer,
            normalLimit + 1);
        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        if (count > normalLimit)
        {
            throw new InvalidOperationException(
                $"Private World '{worldId}' exceeded the bounded limit of {normalLimit} snapshot descriptors.");
        }

        return await _inner.ListWorldSnapshotsAsync(
            ownerProvider,
            ownerExternalId,
            worldId,
            normalLimit,
            cancellationToken);
    }

    public Task<OwnedWorldSnapshotWriteDecision> PublishAsync(
        OwnedWorldSnapshot snapshot,
        CancellationToken cancellationToken = default)
        => _inner.PublishAsync(snapshot, cancellationToken);
}
